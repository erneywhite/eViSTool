using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

/// <summary>
/// Команда «remote»: удалённый доступ для окна на Windows — enable, disable, host, code, new-key, status. То же, что
/// вкладка «Удалённый доступ» окна (ServerRemoteViewModel): файлы agents/&lt;профиль&gt;.remote.json и .remote.pfx через
/// <see cref="RemoteAccess"/>. Работающий агент подхватывает файл сам (проверяет раз в 5 секунд) — команда ждёт этого
/// и говорит итог. Ключ и код в логи не попадают: код печатается только по «remote code», в терминал того, кто спросил.
/// </summary>
internal static class RemoteCommand
{
    private const string HostOption = "--host";
    private const string PortOption = "--port";

    // как часто работающий агент перечитывает файл удалённого доступа (главный цикл в Program.cs) — с запасом
    private static readonly TimeSpan ApplyPeriod = TimeSpan.FromSeconds(6);

    public static int Run(AgentArgs cli, string[] args)
    {
        var w = CliWords.Parse(args, HostOption, PortOption);
        if (w.Error is not null) return w.Bad(Loc.T("remote.cli.usage"));
        if (cli.Help || w.Words.Count == 0)
        {
            (cli.Help ? Console.Out : Console.Error).WriteLine(Loc.T("remote.cli.usage"));
            return cli.Help ? Commands.Ok : Commands.BadUsage;
        }

        var sub = w.Words[0];
        var write = sub switch
        {
            "enable" or "disable" or "new-key" => true,
            "host" => w.Words.Count > 1,
            "code" or "status" => false,
            _ => (bool?)null,
        };
        if (write is null)
        {
            Console.Error.WriteLine(Loc.T("remote.cli.unknown", sub));
            return w.Bad(Loc.T("remote.cli.usage"));
        }
        // --host и --port — только у enable; у host адрес — словом, а не ключом
        if (sub != "enable" && w.Options.Keys.FirstOrDefault() is { } stray)
        {
            Console.Error.WriteLine(Loc.T("ctl.unknownOption", stray));
            return w.Bad(Loc.T("remote.cli.usage"));
        }
        if (w.TooMany(sub == "host" ? 2 : 1)) return w.Bad(Loc.T("remote.cli.usage"));
        // адрес и порт проверяем до всего остального: неверные слова — код 2, ничего не тронуто
        if (sub == "host" && w.Words.Count > 1 && !IsHost(w.Words[1]))
        {
            Console.Error.WriteLine(Loc.T("remote.cli.hostBad", w.Words[1]));
            return Commands.BadUsage;
        }
        if (w[HostOption] is { } h && !IsHost(h))
        {
            Console.Error.WriteLine(Loc.T("remote.cli.hostBad", h));
            return Commands.BadUsage;
        }
        int? port = null;
        if (w[PortOption] is { } p)
        {
            if (!int.TryParse(p, out var n) || n is < RemoteAccess.MinPort or > RemoteAccess.MaxPort)
            {
                Console.Error.WriteLine(Loc.T("remote.cli.portBad", RemoteAccess.MinPort, RemoteAccess.MaxPort));
                return Commands.BadUsage;
            }
            port = n;
        }

        if (AgentUser.AgentsDir(cli, write.Value) is not { } dir) return Commands.Failed;
        var profile = cli.Profile; // ключ --profile, иначе agent.json, иначе «server» — как у самого агента
        try
        {
            return sub switch
            {
                "enable" => Enable(profile, dir, w[HostOption], port),
                "disable" => Disable(profile, dir),
                "host" => w.Words.Count > 1 ? SetHost(profile, dir, w.Words[1]) : ShowHosts(profile, dir),
                "code" => Code(profile, dir),
                "new-key" => NewKey(profile, dir),
                _ => Status(profile, dir),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException)
        {
            Console.Error.WriteLine(ex.Message);
            return Commands.Failed;
        }
    }

    /// <summary>IP-адрес или имя домена — то, что окно подставит в https://&lt;адрес&gt;:&lt;порт&gt;.</summary>
    internal static bool IsHost(string text) =>
        text.Trim() is { Length: > 0 and <= 253 } t && Uri.CheckHostName(t) is UriHostNameType.IPv4 or UriHostNameType.IPv6 or UriHostNameType.Dns;

    private static int Enable(string profile, string dir, string? host, int? port)
    {
        var before = RemoteAccess.Load(profile, dir);
        if (port is { } wanted && wanted != before.Port)
        {
            if (!IsFree(wanted))
            {
                Console.Error.WriteLine(Loc.T("remote.cli.portBusy", wanted));
                return Commands.Failed;
            }
            // Enable оставит порт как есть: он в разрешённом диапазоне
            RemoteAccess.Save(profile, before with { Port = wanted }, dir);
        }
        if (host is not null) RemoteAccess.Save(profile, RemoteAccess.Load(profile, dir) with { Host = host.Trim() }, dir);
        var s = RemoteAccess.Enable(profile, dir);
        Console.WriteLine(Loc.T("remote.cli.enabled", s.Port));
        if (s.Host is { Length: > 0 } saved) Console.WriteLine(Loc.T("remote.cli.hostSet", saved));
        else Console.Error.WriteLine(Loc.T("remote.cli.noHost"));

        // агент слушает сеть, если он запущен; окно в этом месте запустило бы его, без окна это дело службы.
        // Ошибка в первые секунды может остаться от прежней настройки — свою агент покажет после перечитывания файла
        var result = WaitForAgent(profile, dir, (st, waited) => st.RemotePort == s.Port || (st.RemoteError is not null && waited > ApplyPeriod),
            sayIfNone: true);
        ReportAgent(result, s.Port);
        FirewallHint(s.Port);
        if (s.Host is { Length: > 0 }) Console.WriteLine(Loc.T("remote.cli.nextCode"));
        return result is { RemoteError: not null } ? Commands.Failed : Commands.Ok;
    }

    private static int Disable(string profile, string dir)
    {
        var s = RemoteAccess.Disable(profile, dir);
        Console.WriteLine(Loc.T("remote.cli.disabled"));
        if (WaitForAgent(profile, dir, (st, _) => st.RemotePort is null, sayIfNone: false) is { } status)
            Console.WriteLine(status.RemotePort is null ? Loc.T("remote.cli.agentClosed", s.Port) : Loc.T("remote.cli.agentWaiting"));
        return Commands.Ok;
    }

    private static int SetHost(string profile, string dir, string host)
    {
        var s = RemoteAccess.Load(profile, dir) with { Host = host.Trim() };
        RemoteAccess.Save(profile, s, dir);
        Console.WriteLine(Loc.T("remote.cli.hostSet", s.Host));
        if (!s.Enabled) Console.WriteLine(Loc.T("remote.cli.codeOff"));
        return Commands.Ok;
    }

    /// <summary>«remote host» без адреса: что сейчас в коде и какие адреса есть у машины — как список в окне.</summary>
    private static int ShowHosts(string profile, string dir)
    {
        var s = RemoteAccess.Load(profile, dir);
        Console.WriteLine(s.Host is { Length: > 0 } h ? Loc.T("remote.cli.hostCurrent", h) : Loc.T("remote.cli.hostNone"));
        var addresses = RemoteAccess.LocalAddresses();
        Console.WriteLine(addresses.Count > 0 ? Loc.T("remote.cli.addresses") : Loc.T("remote.cli.noAddresses"));
        foreach (var a in addresses) Console.WriteLine("  " + a);
        Console.WriteLine(Loc.T("remote.cli.hostHint"));
        return Commands.Ok;
    }

    /// <summary>
    /// Код подключения — одной строкой в stdout (её вставляют в окно на Windows), предупреждение — в stderr, чтобы
    /// «remote code &gt; файл» или «| xclip» уносили только сам код. Как и окно, без адреса и выключенным код не выдаём.
    /// </summary>
    private static int Code(string profile, string dir)
    {
        var s = RemoteAccess.Load(profile, dir);
        if (!s.Enabled || s.Port <= 0 || s.Key.Length < 32 || !File.Exists(RemoteAccess.CertFile(profile, dir)))
        {
            Console.Error.WriteLine(Loc.T("remote.cli.codeOff"));
            return Commands.Failed;
        }
        if (s.Host is not { Length: > 0 } host)
        {
            Console.Error.WriteLine(Loc.T("remote.cli.noHost"));
            return Commands.Failed;
        }
        // сертификат только читаем: испорченный пересоздаст enable (команда code ничего не пишет — её можно и от root)
        string fingerprint;
        using (var cert = X509CertificateLoader.LoadPkcs12FromFile(RemoteAccess.CertFile(profile, dir), null))
            fingerprint = RemoteAccess.Fingerprint(cert);
        Console.Error.WriteLine(Loc.T("remote.cli.codeWarn"));
        Console.WriteLine(new ConnectionCode(host.Trim(), s.Port, s.Key, fingerprint).ToString());
        return Commands.Ok;
    }

    private static int NewKey(string profile, string dir)
    {
        var s = RemoteAccess.RegenerateKey(profile, dir);
        Console.WriteLine(Loc.T("remote.cli.newKey"));
        if (!s.Enabled) Console.WriteLine(Loc.T("remote.cli.codeOff"));
        return Commands.Ok;
    }

    /// <summary>Включён ли, порт, адрес в коде и слушает ли агент сейчас — без ключа.</summary>
    private static int Status(string profile, string dir)
    {
        var s = RemoteAccess.Load(profile, dir);
        Console.WriteLine(s.Enabled && s.Port > 0 ? Loc.T("remote.cli.stOn", s.Port) : Loc.T("remote.cli.stOff"));
        Console.WriteLine(s.Host is { Length: > 0 } h ? Loc.T("remote.cli.stHost", h) : Loc.T("remote.cli.stNoHost"));
        var status = AgentStatusOf(profile, dir);
        Console.WriteLine(status is null ? Loc.T("remote.cli.stNoAgent")
            : status.RemotePort is { } listening ? Loc.T("remote.cli.stListening", listening)
            : status.RemoteError is { Length: > 0 } error ? Loc.T("remote.cli.stError", error)
            : s.Enabled ? Loc.T("remote.cli.stNotYet") : Loc.T("remote.cli.stClosed"));
        if (s.Enabled && s.Port > 0) FirewallHint(s.Port);
        return Commands.Ok;
    }

    // ---- работающий агент

    private static AgentStatus? AgentStatusOf(string profile, string dir)
    {
        using var client = AgentClient.TryConnect(profile, dir);
        if (client is null) return null;
        try { return client.StatusAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException) { return null; }
    }

    /// <summary>
    /// Агент перечитывает файл раз в 5 секунд — ждём, пока он применит настройку (null — агента нет; sayIfNone —
    /// сказать, что доступ заработает, когда агент запустится).
    /// </summary>
    private static AgentStatus? WaitForAgent(string profile, string dir, Func<AgentStatus, TimeSpan, bool> applied, bool sayIfNone)
    {
        using var client = AgentClient.TryConnect(profile, dir);
        if (client is null)
        {
            if (!sayIfNone) return null;
            Console.WriteLine(Loc.T("remote.cli.agentNotRunning"));
            Console.WriteLine(Loc.T("ctl.startAgentHint"));
            return null;
        }
        for (var started = DateTime.Now; ; Thread.Sleep(500))
        {
            AgentStatus last;
            try { last = client.StatusAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException) { return null; }
            var waited = DateTime.Now - started;
            if (applied(last, waited) || waited > 3 * ApplyPeriod) return last;
        }
    }

    private static void ReportAgent(AgentStatus? status, int port)
    {
        if (status is null) return;
        Console.WriteLine(status.RemotePort == port ? Loc.T("remote.cli.agentListening", port)
            : status.RemoteError is { } error ? Loc.T("remote.cli.agentError", port, error)
            : Loc.T("remote.cli.agentWaiting"));
    }

    /// <summary>Свободен ли порт: проба на локальном адресе, как у <see cref="RemoteAccess.FreePort"/>.</summary>
    private static bool IsFree(int port)
    {
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Брандмауэр на Linux не трогаем (это настройка системы), но если он включён — подсказываем, как открыть порт.
    /// ufw — по его файлу настроек (читается без root), firewalld — по «firewall-cmd --state».
    /// </summary>
    private static void FirewallHint(int port)
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            if (File.Exists("/etc/ufw/ufw.conf")
                && File.ReadLines("/etc/ufw/ufw.conf").Any(l => l.Trim().Replace(" ", "").Equals("ENABLED=yes", StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine(Loc.T("remote.cli.firewallUfw", port));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (FirewalldRunning()) Console.WriteLine(Loc.T("remote.cli.firewallFirewalld", port));
    }

    private static bool FirewalldRunning()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("firewall-cmd")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--state");
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            return p.WaitForExit(5000) && output.Result.Trim() == "running";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false; // firewalld не стоит
        }
    }
}

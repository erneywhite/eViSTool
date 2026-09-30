using System.Diagnostics;
using System.Text;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server.Remote;
using Newtonsoft.Json;

namespace eViSTool.Core.Server;

/// <summary>Клиент агента: окно eViSTool управляет сервером через него.</summary>
public sealed class AgentClient : IDisposable
{
    private readonly HttpClient _http;

    public AgentEndpoint Endpoint { get; }

    public AgentClient(AgentEndpoint endpoint, string key) : this(endpoint, new Uri($"http://127.0.0.1:{endpoint.Port}/"), key, null) { }

    private AgentClient(AgentEndpoint endpoint, Uri baseAddress, string key, string? fingerprint)
    {
        Endpoint = endpoint;
        var handler = new HttpClientHandler();
        // удалённый агент — только тот, чей сертификат указан в коде подключения: подменить его по дороге не выйдет
        if (fingerprint is not null)
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert is not null && string.Equals(RemoteAccess.Fingerprint(cert), fingerprint, StringComparison.OrdinalIgnoreCase);
        _http = new HttpClient(handler)
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(40), // долгий опрос консоли — до 30 с
        };
        _http.DefaultRequestHeaders.Add(AgentProtocol.KeyHeader, key);
    }

    /// <summary>Агент на другой машине — по коду подключения (HTTPS, сертификат сверяется с отпечатком из кода).</summary>
    public static AgentClient ForRemote(ConnectionCode code) =>
        new(new AgentEndpoint(0, code.Port, default, ""), new UriBuilder(Uri.UriSchemeHttps, code.Host, code.Port).Uri, code.Key, code.Fingerprint);

    /// <summary>Подключиться к уже работающему агенту профиля (null — агента нет).</summary>
    public static AgentClient? TryConnect(string profileId, string? agentsDir = null)
    {
        var file = AgentProtocol.StateFile(profileId, agentsDir);
        if (!File.Exists(file)) return null;
        try
        {
            var endpoint = JsonConvert.DeserializeObject<AgentEndpoint>(File.ReadAllText(file));
            if (endpoint is null || !IsAlive(endpoint.Pid)) return null;
            return new AgentClient(endpoint, AgentProtocol.GetOrCreateKey(profileId, agentsDir));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("eViSTool.Agent", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public Task<AgentStatus> StatusAsync(CancellationToken ct = default) => Get<AgentStatus>("status", ct);

    /// <summary>Строки консоли новее since; если их нет — агент подождёт до wait секунд.</summary>
    public Task<List<ConsoleLine>> ConsoleAsync(long since, int waitSeconds = 25, CancellationToken ct = default) =>
        Get<List<ConsoleLine>>($"console?since={since}&wait={waitSeconds}", ct);

    public Task<AgentStatus> StartAsync(CancellationToken ct = default) => Post("start", null, ct);
    public Task<AgentStatus> StopAsync(CancellationToken ct = default) => Post("stop", null, ct);
    public Task<AgentStatus> RestartAsync(CancellationToken ct = default) => Post("restart", null, ct);
    public Task<AgentStatus> KillAsync(CancellationToken ct = default) => Post("kill", null, ct);
    public Task<AgentStatus> ShutdownAsync(CancellationToken ct = default) => Post("shutdown", null, ct);
    public Task<AgentStatus> CommandAsync(string text, CancellationToken ct = default) => Post("command", new CommandRequest(text), ct);

    /// <summary>Сделать копию мира сейчас (сервер должен работать).</summary>
    public Task<AgentStatus> BackupAsync(CancellationToken ct = default) => Post("backup", null, ct);

    private async Task<T> Get<T>(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
        return await Read<T>(resp, ct).ConfigureAwait(false);
    }

    private async Task<AgentStatus> Post(string path, object? body, CancellationToken ct)
    {
        using var content = new StringContent(body is null ? "" : JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(path, content, ct).ConfigureAwait(false);
        return await Read<AgentStatus>(resp, ct).ConfigureAwait(false);
    }

    private static async Task<T> Read<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // агент отдаёт ошибки как problem+json: {"detail": "…"}
            var detail = TryDetail(text) ?? $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
            throw new InvalidOperationException(detail);
        }
        return JsonConvert.DeserializeObject<T>(text) ?? throw new InvalidOperationException("empty response");
    }

    private static string? TryDetail(string text)
    {
        try { return Newtonsoft.Json.Linq.JObject.Parse(text)["detail"]?.ToString(); }
        catch (JsonException) { return null; }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Запуск агента для серверного профиля (или подключение к уже работающему).</summary>
public static class AgentLauncher
{
    public static string ServerExe(GameProfile profile) =>
        Path.Combine(profile.GameDir ?? "", "VintagestoryServer.exe");

    /// <summary>Агент профиля: работающий — подключиться, нет — запустить (с --start сразу запустит и сервер).</summary>
    public static async Task<AgentClient> EnsureRunningAsync(GameProfile profile, bool startServer,
        string? agentExe = null, string? agentsDir = null, CancellationToken ct = default)
    {
        if (AgentClient.TryConnect(profile.Id, agentsDir) is { } existing)
        {
            try
            {
                await existing.StatusAsync(ct).ConfigureAwait(false);
                if (startServer) await existing.StartAsync(ct).ConfigureAwait(false);
                return existing;
            }
            catch (HttpRequestException)
            {
                existing.Dispose(); // файл остался от упавшего агента — запускаем новый
            }
        }

        if (string.IsNullOrWhiteSpace(profile.DataDir)) throw new InvalidOperationException(Loc.T("profile.dataNotFound"));
        var exe = ServerExe(profile);
        if (!File.Exists(exe)) throw new FileNotFoundException(Loc.T("srv.exeNotFound", exe), exe);

        agentExe ??= Path.Combine(AppContext.BaseDirectory, AgentProtocol.ExeName);
        if (!File.Exists(agentExe)) throw new FileNotFoundException(Loc.T("srv.agentMissing", agentExe), agentExe);

        var psi = new ProcessStartInfo(agentExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true, // своя скрытая консоль: в ней сервер пишет UTF-8 и получает Ctrl+C
            WorkingDirectory = Path.GetDirectoryName(agentExe)!,
        };
        psi.ArgumentList.Add("--profile"); psi.ArgumentList.Add(profile.Id);
        psi.ArgumentList.Add("--exe"); psi.ArgumentList.Add(exe);
        psi.ArgumentList.Add("--data"); psi.ArgumentList.Add(profile.DataDir);
        if (agentsDir is not null) { psi.ArgumentList.Add("--agents-dir"); psi.ArgumentList.Add(agentsDir); }
        if (startServer) psi.ArgumentList.Add("--start");
        // имя профиля — в имена резервных копий: по файлу видно, чей это мир
        psi.ArgumentList.Add("--backup-name"); psi.ArgumentList.Add(BackupStore.Slug(profile.Name));
        // сообщения агента (запуск, сторож, остановка) — на языке окна
        psi.ArgumentList.Add("--lang"); psi.ArgumentList.Add(Loc.Instance.Language);

        var stateFile = AgentProtocol.StateFile(profile.Id, agentsDir);
        try { File.Delete(stateFile); } catch (IOException) { }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException(Loc.T("srv.agentStartFailed"));
        var until = DateTime.Now.AddSeconds(20);
        while (DateTime.Now < until)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException(Loc.T("srv.agentExited", process.ExitCode));
            if (AgentClient.TryConnect(profile.Id, agentsDir) is { } client && client.Endpoint.Pid == process.Id)
                return client;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        throw new TimeoutException(Loc.T("srv.agentTimeout"));
    }
}

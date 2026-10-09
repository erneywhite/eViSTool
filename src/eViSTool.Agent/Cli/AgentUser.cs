using System.Diagnostics;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;

/// <summary>
/// Чьи файлы агента. На Linux агент работает от владельца данных сервера (vintagestory), его ключи и настройки лежат
/// в data/agents с правами только для него. Команды, которые их читают или пишут, должны работать от того же
/// пользователя: от root они создали бы файлы, которых агент не прочитает, от другого — не прочитали бы сами.
/// Чужой пользователь — подсказка «sudo -u &lt;владелец&gt; …» с той же командой. На Windows окно, агент и команды —
/// всегда один пользователь, проверять нечего.
/// </summary>
internal static class AgentUser
{
    /// <summary>
    /// Папка agents для команды. write — команда пишет файлы агента (remote enable, host, new-key): от чужого
    /// пользователя, даже от root, — нельзя. Читать root может (status, remote code). null — нельзя, подсказка уже в stderr.
    /// </summary>
    public static string? AgentsDir(AgentArgs cli, bool write)
    {
        if (OperatingSystem.IsWindows()) return cli.AgentsDir ?? AgentProtocol.DefaultAgentsDir;

        var me = Environment.UserName;
        // папка данных рядом с программой — её и возьмёт агент (AppPaths); узнаём её, ничего не создавая:
        // AppPaths.Root создал бы data от того, кто запустил команду, и агент потом не смог бы в неё писать
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        var agents = Path.Combine(portable, "agents");
        string? owner = null;
        if (cli.AgentsDir is { } given)
            owner = Directory.Exists(given) ? Owner(given) : null;
        else if (Directory.Exists(portable))
            owner = Owner(Directory.Exists(agents) ? agents : portable);
        else if (write && Owner(AppContext.BaseDirectory) is { } dirOwner && dirOwner != "root")
            // данных ещё нет, и команда создаст их от себя — а папку программы отдали пользователю сервера.
            // Папка программы root-а (скажем, /opt) — не довод: туда не пишет и агент, его данные — в его домашней папке
            owner = dirOwner;

        if (owner is not null && owner != me)
        {
            // root прочитает что угодно — смотреть ему можно, писать (создавать файлы своими) — нет
            if (Environment.IsPrivilegedProcess && !write) return cli.AgentsDir ?? agents;
            Console.Error.WriteLine(Loc.T("ctl.wrongUser", owner, me, SameCommandAs(owner)));
            return null;
        }
        if (cli.AgentsDir is { } dir) return dir;
        if (write || Directory.Exists(portable)) return AgentProtocol.DefaultAgentsDir;
        // только посмотреть, а данных рядом нет: агент этого пользователя мог писать в запасную папку, но создавать
        // ради «status» ни одну из них не нужно
        return Directory.Exists(eViSTool.Core.AppPaths.FallbackRoot) ? Path.Combine(eViSTool.Core.AppPaths.FallbackRoot, "agents") : agents;
    }

    /// <summary>Владелец файла или папки (Linux): stat из coreutils — в .NET своего способа узнать владельца нет.</summary>
    public static string? Owner(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("stat")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "-c", "%U", "--", path }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000)) return null;
            var name = output.Result.Trim();
            return p.ExitCode == 0 && name.Length > 0 && name != "UNKNOWN" ? name : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null; // нет stat — не проверяем
        }
    }

    /// <summary>Та же команда от владельца: «sudo -u vintagestory /home/…/eViSTool.Agent remote enable --host …».</summary>
    private static string SameCommandAs(string owner) =>
        string.Join(" ", new[] { "sudo", "-u", owner, Environment.ProcessPath ?? AgentProtocol.ExeName }
            .Concat(Environment.GetCommandLineArgs().Skip(1)).Select(Quote));

    /// <summary>Слово для оболочки: с пробелами и особыми знаками — в одинарных кавычках.</summary>
    internal static string Quote(string word) =>
        word.Length > 0 && word.All(c => char.IsAsciiLetterOrDigit(c) || "-_./:=@%+,".Contains(c))
            ? word
            : "'" + word.Replace("'", "'\\''") + "'";
}

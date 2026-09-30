using System.Diagnostics;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Server.Config;

/// <summary>
/// Создаёт serverconfig.json по умолчанию силами самого сервера: «--setconfig {}» записывает конфиг, если его нет,
/// и сразу выходит — сервер не запускается, мир не создаётся. Так новый профиль можно настроить до первого запуска.
/// </summary>
public static class ServerConfigGenerator
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Создать конфиг в папке данных. Уже есть — ничего не делает (существующий файл не трогается).
    /// Ошибки — InvalidOperationException / FileNotFoundException с понятным текстом.
    /// </summary>
    public static async Task GenerateAsync(string serverExe, string dataDir, CancellationToken ct = default)
    {
        var config = Path.Combine(dataDir, ProfileResolver.ServerConfigName);
        if (File.Exists(config)) return;
        if (!File.Exists(serverExe)) throw new FileNotFoundException(Loc.T("srv.exeNotFound", serverExe), serverExe);

        Directory.CreateDirectory(dataDir);
        var psi = new ProcessStartInfo(serverExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(serverExe)!,
        };
        psi.ArgumentList.Add("--dataPath");
        psi.ArgumentList.Add(dataDir);
        // пустой набор правок: нужен только побочный эффект — «создать конфиг, если его нет»
        psi.ArgumentList.Add("--setconfig");
        psi.ArgumentList.Add("{}");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException(Loc.T("srvcfg.genFailed", serverExe));
        // вывод читаем, чтобы сервер не упёрся в заполненный канал; хвост пригодится для текста ошибки
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(Loc.T("srvcfg.genTimeout", (int)Timeout.TotalSeconds));
        }

        // код выхода у «--setconfig» ненулевой и при успехе — успех определяем по файлу
        if (File.Exists(config))
        {
            UseSharedMods(config, dataDir);
            return;
        }
        var tail = string.Join(Environment.NewLine,
            ((await output.ConfigureAwait(false)) + Environment.NewLine + (await errors.ConfigureAwait(false)))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(4));
        throw new InvalidOperationException(Loc.T("srvcfg.genNoFile", tail));
    }

    /// <summary>
    /// Профиль лежит в ServerProfiles, и у «дома» есть папка модов — свежий конфиг сразу смотрит в неё,
    /// а не в собственную пустую Mods (моды общие для всех профилей, пока не решили иначе).
    /// </summary>
    private static void UseSharedMods(string config, string dataDir)
    {
        if (!ServerProfileLayout.IsInContainer(dataDir)) return;
        var shared = ServerProfileLayout.SharedModsDir(dataDir);
        if (!Directory.Exists(shared)) return;

        try
        {
            var root = ModConfigEditor.Load(config);
            var own = Path.GetFullPath(Path.Combine(dataDir, "Mods"));
            var paths = new JArray();
            foreach (var entry in root["ModPaths"] as JArray ?? new JArray())
            {
                var text = entry.Type == JTokenType.String ? entry.ToString() : null;
                var isOwn = text is not null && Path.IsPathRooted(text)
                            && string.Equals(Path.GetFullPath(text).TrimEnd('\\', '/'), own, StringComparison.OrdinalIgnoreCase);
                if (!isOwn) paths.Add(entry.DeepClone());
            }
            paths.Add(shared);
            root["ModPaths"] = paths;
            ModConfigEditor.Save(config, root);
            File.Delete(config + ".evistool.bak"); // копия только что созданного файла никому не нужна
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            // не получилось — останется конфиг по умолчанию со своей папкой модов: работать будет
        }
    }
}

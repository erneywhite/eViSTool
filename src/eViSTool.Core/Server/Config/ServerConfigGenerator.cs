using System.Diagnostics;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;

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
        if (File.Exists(config)) return;
        var tail = string.Join(Environment.NewLine,
            ((await output.ConfigureAwait(false)) + Environment.NewLine + (await errors.ConfigureAwait(false)))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(4));
        throw new InvalidOperationException(Loc.T("srvcfg.genNoFile", tail));
    }
}

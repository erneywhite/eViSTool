using eViSTool.Core.Game;

namespace eViSTool.Core.Tests;

/// <summary>
/// Поддельный сервер VS (tests/FakeVsServer) — собирается вместе с тестами (ProjectReference без ссылки на сборку).
/// Запускается тем же путём, что и настоящий: на Windows — FakeVsServer.exe, на Linux — dotnet FakeVsServer.dll.
/// </summary>
internal static class FakeServer
{
    public static readonly string Bin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "FakeVsServer", "bin", "Debug", "net10.0"));

    /// <summary>Файл поддельного сервера для ServerHost на этой платформе.</summary>
    public static string ServerFile => Path.Combine(Bin, OperatingSystem.IsWindows() ? "FakeVsServer.exe" : "FakeVsServer.dll");

    /// <summary>
    /// «Папка игры»: поддельный сервер под именем настоящего (VintagestoryServer.exe / VintagestoryServer.dll). Возвращает
    /// путь к файлу сервера — тот же, что ServerExecutable.PathIn(папка).
    /// </summary>
    public static string InstallAs(string gameDir)
    {
        if (!OperatingSystem.IsWindows()) return InstallDllAs(gameDir);
        CopyBin(gameDir);
        var exe = ServerExecutable.PathIn(gameDir);
        File.Copy(ServerFile, exe, overwrite: true); // exe-запускатель сам найдёт FakeVsServer.dll
        return exe;
    }

    /// <summary>Сервер в виде VintagestoryServer.dll (как на Linux) на любой платформе: dll и её runtimeconfig.json для dotnet.</summary>
    public static string InstallDllAs(string gameDir)
    {
        CopyBin(gameDir);
        var dll = Path.Combine(gameDir, ServerExecutable.DllName);
        File.Copy(Path.Combine(Bin, "FakeVsServer.dll"), dll, overwrite: true);
        File.Copy(Path.Combine(Bin, "FakeVsServer.runtimeconfig.json"), Path.ChangeExtension(dll, ".runtimeconfig.json"), overwrite: true);
        return dll;
    }

    private static void CopyBin(string gameDir)
    {
        Directory.CreateDirectory(gameDir);
        foreach (var f in Directory.GetFiles(Bin)) File.Copy(f, Path.Combine(gameDir, Path.GetFileName(f)), overwrite: true);
    }
}

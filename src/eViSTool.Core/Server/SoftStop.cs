using System.Runtime.InteropServices;

namespace eViSTool.Core.Server;

/// <summary>
/// Мягкая остановка сервера по PID: сервер VS сохраняет мир и выходит, в отличие от kill. На Windows это Ctrl+C в его
/// консоль, на Linux — SIGTERM: на него сервер отвечает так же (сохраняет мир, «All threads gracefully shut down»),
/// так его останавливает и systemd.
/// </summary>
public static partial class SoftStop
{
    private const int SIGTERM = 15;

    /// <summary>
    /// Сервер, запущенный не нами (остался от ViSST, запущен server.sh или руками). false — сигнал не ушёл: процесса уже
    /// нет, он другого пользователя (на Linux SIGTERM чужому пользователю может послать только root), а на Windows —
    /// ещё и если у нас самих есть консоль (Ctrl+C в чужую консоль шлёт только процесс без своей, окно eViSTool).
    /// </summary>
    public static bool Foreign(int pid)
    {
        // PID 0 и отрицательные не принимаем: kill понял бы их как группу процессов или вообще все процессы,
        // а AttachConsole(-1) — как консоль нашего родителя
        if (pid <= 0) return false;
        return OperatingSystem.IsWindows() ? ConsoleInterop.SendCtrlCToForeignProcess(pid) : Signal(pid);
    }

    /// <summary>Свой сервер, дочерний процесс агента: на Windows — Ctrl+C через общую консоль, на Linux — SIGTERM ему.</summary>
    public static bool Child(int pid)
    {
        if (pid <= 0) return false;
        return OperatingSystem.IsWindows() ? ConsoleInterop.SendCtrlCToConsole() : Signal(pid);
    }

    private static bool Signal(int pid) =>
        (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()) && kill(pid, SIGTERM) == 0;

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int signal);
}

using System.Runtime.InteropServices;

/// <summary>
/// Агента запустили двойным щелчком (Windows): консоль создана только под него — в ней нет других процессов. Из
/// терминала, службы или окна eViSTool консоль общая (или её нет), и это не срабатывает.
/// </summary>
internal static class DoubleClick
{
    public static bool Detected()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected) return false;
        try
        {
            var ids = new uint[4];
            return GetConsoleProcessList(ids, (uint)ids.Length) == 1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
}

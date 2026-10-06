using System.Runtime.InteropServices;

namespace eViSTool.Core.Game;

/// <summary>
/// Командная строка чужого процесса (Windows 8.1+, без WMI): по ней отличаем копии игры с разными папками данных
/// (<c>--dataPath</c>), запущенные из одной папки игры.
/// </summary>
public static class ProcessCommandLine
{
    private const int ProcessCommandLineInformation = 60;
    private const uint QueryLimitedInformation = 0x1000;

    /// <summary>Командная строка процесса; null — не узнать (нет прав, процесс уже завершился).</summary>
    public static string? Get(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var size);
            if (size <= 0 || size > 1 << 20) return null;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, size, out _) != 0) return null;
                // UNICODE_STRING: длина в байтах, ёмкость, указатель на текст (сразу за структурой)
                var length = Marshal.ReadInt16(buffer);
                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return length <= 0 || text == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text, length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Значение параметра (<c>--dataPath "C:\x y"</c>) из командной строки; null — параметра нет.</summary>
    public static string? Argument(string commandLine, string name)
    {
        var args = Split(commandLine);
        var i = args.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) return i + 1 < args.Count ? args[i + 1] : "";
        // и в виде --dataPath=C:\x
        return args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..];
    }

    /// <summary>Разбор как у Windows: пробелы делят, кавычки склеивают, \" — кавычка внутри.</summary>
    public static List<string> Split(string commandLine)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var any = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
            {
                current.Append('"');
                i++;
                any = true;
            }
            else if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) result.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }
        if (any) result.Add(current.ToString());
        return result;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, IntPtr info, int length, out int returned);
}

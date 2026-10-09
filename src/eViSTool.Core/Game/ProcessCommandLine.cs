using System.Runtime.InteropServices;

namespace eViSTool.Core.Game;

/// <summary>
/// Командная строка чужого процесса: по ней отличаем копии игры с разными папками данных (<c>--dataPath</c>), запущенные
/// из одной папки игры. Windows 8.1+ — через ntdll, без WMI; Linux — из /proc/&lt;pid&gt;/cmdline.
/// </summary>
public static class ProcessCommandLine
{
    private const int ProcessCommandLineInformation = 60;
    private const uint QueryLimitedInformation = 0x1000;

    /// <summary>
    /// Командная строка процесса одной строкой; null — не узнать (нет прав, процесс уже завершился). На Linux склеена из
    /// аргументов, с кавычками вокруг тех, где есть пробелы, — её разбирает тот же <see cref="Split"/>.
    /// </summary>
    public static string? Get(int pid)
    {
        if (OperatingSystem.IsLinux()) return GetArgs(pid) is { } args ? Join(args) : null;
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

    /// <summary>
    /// Аргументы процесса по отдельности, первый — сама программа; null — не узнать. На Linux — ровно как их передали
    /// (cmdline хранит их через \0), на Windows — разбор командной строки.
    /// </summary>
    public static IReadOnlyList<string>? GetArgs(int pid)
    {
        if (!OperatingSystem.IsLinux()) return Get(pid) is { } cmd ? Split(cmd) : null;
        try
        {
            return ParseProcCmdline(File.ReadAllBytes($"/proc/{pid}/cmdline"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // процесса уже нет (или /proc от нас закрыт)
        }
    }

    /// <summary>Содержимое /proc/&lt;pid&gt;/cmdline: аргументы в UTF-8, каждый заканчивается \0. Пустое — у потоков ядра и зомби.</summary>
    public static List<string> ParseProcCmdline(ReadOnlySpan<byte> raw)
    {
        if (raw.Length > 0 && raw[^1] == 0) raw = raw[..^1];
        return raw.Length == 0 ? [] : [.. System.Text.Encoding.UTF8.GetString(raw).Split('\0')];
    }

    /// <summary>Рабочая папка процесса (Linux, /proc/&lt;pid&gt;/cwd); null — не узнать: Windows, чужой пользователь, процесса нет.</summary>
    public static string? WorkingDirectory(int pid)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            return new DirectoryInfo($"/proc/{pid}/cwd").LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Значение параметра (<c>--dataPath "C:\x y"</c>) из командной строки; null — параметра нет.</summary>
    public static string? Argument(string commandLine, string name) => Argument(Split(commandLine), name);

    /// <summary>Значение параметра из готового списка аргументов; null — параметра нет.</summary>
    public static string? Argument(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return i + 1 < args.Count ? args[i + 1] : "";
        // и в виде --dataPath=C:\x
        return args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..];
    }

    /// <summary>Склеить аргументы в строку, которую <see cref="Split"/> разберёт обратно.</summary>
    private static string Join(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a => a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c == '"')
            ? a
            : "\"" + a.Replace("\"", "\\\"") + "\""));

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

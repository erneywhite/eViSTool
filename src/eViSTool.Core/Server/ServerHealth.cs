using System.Runtime.InteropServices;

namespace eViSTool.Core.Server;

/// <summary>
/// «Сервер не успевает»: сервер пишет «Server overloaded. A tick took 513ms…», когда такт обработки мира длится дольше нормы.
/// Одно такое предупреждение — ерунда (подгрузка мира, сохранение); много за короткое время — сервер не справляется.
/// Сообщаем, когда их <see cref="Threshold"/> и больше за <see cref="Window"/>, и не чаще раза в <see cref="Quiet"/>.
/// </summary>
public sealed class OverloadWatch
{
    public const int Threshold = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Quiet = TimeSpan.FromHours(1);

    private readonly Queue<DateTime> _hits = new();
    private DateTime _lastReport = DateTime.MinValue;

    public static bool IsOverloadLine(string line) => line.Contains("Server overloaded", StringComparison.Ordinal);

    /// <summary>Строка консоли. Возвращает число предупреждений за окно, если пора сообщить, иначе null.</summary>
    public int? Add(string line, DateTime now)
    {
        if (!IsOverloadLine(line)) return null;
        _hits.Enqueue(now);
        while (_hits.Count > 0 && now - _hits.Peek() > Window) _hits.Dequeue();
        if (_hits.Count < Threshold || now - _lastReport < Quiet) return null;
        _lastReport = now;
        return _hits.Count;
    }
}

/// <summary>«Мало места на диске»: сообщаем один раз, когда свободного меньше порога; снова — только после того, как место освободилось.</summary>
public sealed class LowDiskWatch
{
    public const long ThresholdBytes = 5L * 1024 * 1024 * 1024;
    // освободилось с запасом — считаем, что проблема ушла (чтобы не сообщать на каждом колебании у границы)
    private const long RecoverBytes = ThresholdBytes + 1L * 1024 * 1024 * 1024;

    private bool _reported;

    /// <summary>Свободно столько-то байт. true — пора сообщить.</summary>
    public bool Check(long freeBytes)
    {
        if (freeBytes >= RecoverBytes) _reported = false;
        if (freeBytes >= ThresholdBytes || _reported) return false;
        _reported = true;
        return true;
    }

    /// <summary>Свободное место на диске с этой папкой; не узнать — null.</summary>
    public static long? FreeBytes(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}

/// <summary>Свободная оперативная память компьютера — для подсказки, отчего сервер не успевает.</summary>
public static partial class SystemMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>(занято %, свободно МБ); не узнать — null.</summary>
    public static (int LoadPercent, long FreeMb)? Status()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref s) ? ((int)s.MemoryLoad, (long)(s.AvailPhys / (1024 * 1024))) : null;
    }

    /// <summary>Памяти мало: занято больше 90% или свободно меньше 1 ГБ.</summary>
    public static bool IsLow((int LoadPercent, long FreeMb) s) => s.LoadPercent >= 90 || s.FreeMb < 1024;
}

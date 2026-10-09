using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace eViSTool.Core.Platform;

/// <summary>
/// Пользователи Linux: от чьего имени работает процесс и чья папка. Агент на сервере работает от владельца данных
/// (vintagestory), а команды запускают и через sudo: файлы, созданные от root, служба потом не сможет менять.
/// </summary>
[UnsupportedOSPlatform("windows")]
public static partial class UnixAccount
{
    private const int AtFdCwd = -100;
    private const uint StatxMode = 0x2, StatxUid = 0x8;
    // struct statx одинакова на всех архитектурах: stx_uid — со смещения 20, stx_mode — с 28
    private const int StatxSize = 256, StatxUidOffset = 20, StatxModeOffset = 28;

    /// <summary>Действующий пользователь процесса (euid): после sudo -u — тот, от чьего имени запустили.</summary>
    public static uint CurrentUid => geteuid();

    public static bool IsRoot => geteuid() == 0;

    /// <summary>Владелец файла или папки (uid; ссылка — владелец того, на что она указывает); null — не узнать.</summary>
    public static uint? OwnerOf(string path) => Stat(path)?.Uid;

    /// <summary>Владелец и права (ссылка — того, на что она указывает); null — нет такого или не узнать.</summary>
    public static unsafe (uint Uid, UnixFileMode Mode)? Stat(string path)
    {
        var buf = stackalloc byte[StatxSize];
        try
        {
            if (statx(AtFdCwd, path, 0, StatxUid | StatxMode, buf) != 0) return null;
            return (*(uint*)(buf + StatxUidOffset), (UnixFileMode)(*(ushort*)(buf + StatxModeOffset) & 0xFFF));
        }
        catch (EntryPointNotFoundException)
        {
            return null; // glibc старше 2.28
        }
    }

    /// <summary>Имя пользователя по uid; null — такого нет в системе.</summary>
    public static string? NameOf(uint uid)
    {
        var entry = getpwuid(uid); // struct passwd, первое поле — имя
        return entry == 0 ? null : Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(entry));
    }

    /// <summary>Имя, а если его нет — номер: так и пишем в подсказках (chown понимает оба).</summary>
    public static string Describe(uint uid) => NameOf(uid) ?? uid.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [LibraryImport("libc")]
    private static partial uint geteuid();

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int statx(int dirfd, string path, int flags, uint mask, byte* buffer);

    [LibraryImport("libc")]
    private static partial nint getpwuid(uint uid);
}

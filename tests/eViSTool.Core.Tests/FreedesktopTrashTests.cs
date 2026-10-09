using eViSTool.Core.Platform;

namespace eViSTool.Core.Tests;

/// <summary>Корзина Linux по freedesktop.org: files + info/*.trashinfo, из неё можно вернуть.</summary>
public sealed class FreedesktopTrashTests : IDisposable
{
    private static readonly DateTime When = new(2026, 10, 9, 19, 30, 5);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evistool-trash-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _extra = [];

    private string Trash => Path.Combine(_dir, "Trash");

    public void Dispose()
    {
        foreach (var dir in _extra.Append(_dir))
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private string MakeFile(string relative, string text)
    {
        var path = Path.Combine(_dir, "data", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Строка Path= из .trashinfo — обратно в путь.</summary>
    private static string OriginalPath(string infoFile) =>
        Uri.UnescapeDataString(File.ReadAllLines(infoFile).Single(l => l.StartsWith("Path=", StringComparison.Ordinal))[5..]);

    [Fact]
    public void File_GoesToFiles_WithInfo_AndCanBePutBack()
    {
        var src = MakeFile("мир 1.vcdbs", "world");

        var inTrash = FreedesktopTrash.Send(src, [Trash], When);

        Assert.False(File.Exists(src));
        Assert.Equal(Path.Combine(Trash, "files", "мир 1.vcdbs"), inTrash);
        Assert.Equal("world", File.ReadAllText(inTrash));
        var info = Path.Combine(Trash, "info", "мир 1.vcdbs.trashinfo");
        Assert.Equal($"[Trash Info]\nPath={FreedesktopTrash.Escape(Path.GetFullPath(src))}\nDeletionDate=2026-10-09T19:30:05\n", File.ReadAllText(info));
        Assert.Equal(Path.GetFullPath(src), OriginalPath(info));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Trash));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(info));
        }

        // то же имя ещё раз — рядом под другим (как у GLib: номер перед расширением), первое не тронуто
        File.WriteAllText(src, "world 2");
        var second = FreedesktopTrash.Send(src, [Trash], When);
        Assert.Equal(Path.Combine(Trash, "files", "мир 1.2.vcdbs"), second);
        Assert.Equal(Path.GetFullPath(src), OriginalPath(Path.Combine(Trash, "info", "мир 1.2.vcdbs.trashinfo")));
        Assert.Equal("world", File.ReadAllText(inTrash));

        // обратимо: по Path= файл возвращается на место
        File.Move(inTrash, OriginalPath(info));
        Assert.Equal("world", File.ReadAllText(src));
    }

    [Fact]
    public void Folder_GoesWhole()
    {
        var src = Path.GetDirectoryName(MakeFile(Path.Combine("Mods", "carryon", "assets", "a.json"), "{}"))!;
        src = Path.GetDirectoryName(src)!; // …/Mods/carryon

        var inTrash = FreedesktopTrash.Send(src + Path.DirectorySeparatorChar, [Trash], When);

        Assert.False(Directory.Exists(src));
        Assert.Equal(Path.Combine(Trash, "files", "carryon"), inTrash);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(inTrash, "assets", "a.json")));
        Assert.Equal(Path.GetFullPath(src), OriginalPath(Path.Combine(Trash, "info", "carryon.trashinfo")));
    }

    [Fact]
    public void UnwritableTrash_TheNextOneIsUsed_MissingFile_LeavesNoTrace()
    {
        Directory.CreateDirectory(_dir);
        var blocked = Path.Combine(_dir, "home");
        File.WriteAllText(blocked, ""); // «домашняя папка» — файл: корзину в ней не создать
        var spare = Path.Combine(_dir, "data", "trash");
        var src = MakeFile("backup.vcdbs", "b");

        var inTrash = FreedesktopTrash.Send(src, [Path.Combine(blocked, "Trash"), spare], When);
        Assert.Equal(Path.Combine(spare, "files", "backup.vcdbs"), inTrash);

        Assert.Throws<FileNotFoundException>(() => FreedesktopTrash.Send(Path.Combine(_dir, "нет такого"), [spare], When));
        Assert.Single(Directory.GetFiles(Path.Combine(spare, "info")));
    }

    [Fact]
    public void FolderFromAnotherPartition_IsCopiedWhole()
    {
        // /dev/shm — память, отдельный раздел: rename туда-обратно не работает, папку приходится копировать
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev/shm")) return;
        var src = Path.Combine("/dev/shm", "evistool-trash-" + Guid.NewGuid().ToString("N"));
        _extra.Add(src);
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "x.txt"), "x");
        File.WriteAllText(Path.Combine(src, "y.txt"), "y");

        var inTrash = FreedesktopTrash.Send(src, [Trash], When);

        Assert.False(Directory.Exists(src));
        Assert.Equal("x", File.ReadAllText(Path.Combine(inTrash, "sub", "x.txt")));
        Assert.Equal("y", File.ReadAllText(Path.Combine(inTrash, "y.txt")));
        Assert.Single(Directory.GetFiles(Path.Combine(Trash, "info")));
    }

    [Fact]
    public void PathIsEscapedLikeAUrl()
    {
        Assert.Equal("/home/vs/%D0%BC%D0%B8%D1%80%201/a%25b%23c.zip", FreedesktopTrash.Escape("/home/vs/мир 1/a%b#c.zip"));
        Assert.Equal("/var/vintagestory/data/Backups/a-b_c.d~e", FreedesktopTrash.Escape("/var/vintagestory/data/Backups/a-b_c.d~e"));
    }

    [Fact]
    public void UserTrash_IsInXdgDataHome_OrInHome_NotForServiceUsers()
    {
        Directory.CreateDirectory(_dir);
        Assert.Equal("/srv/xdg", XdgDirs.DataHome("/srv/xdg", _dir));
        Assert.Equal(Path.Combine(Path.GetFullPath(_dir), ".local", "share"), XdgDirs.DataHome("relative/xdg", _dir)); // относительный — не в счёт
        Assert.Equal(Path.Combine(Path.GetFullPath(_dir), ".local", "share"), XdgDirs.DataHome(null, _dir));
        Assert.Null(XdgDirs.DataHome(null, Path.Combine(_dir, "nonexistent")));
        Assert.Null(XdgDirs.DataHome(null, Path.GetPathRoot(Path.GetFullPath(_dir))));
        Assert.Null(XdgDirs.DataHome("", ""));
    }
}

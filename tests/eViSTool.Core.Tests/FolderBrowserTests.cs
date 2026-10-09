using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

/// <summary>Обзор папок на машине с сервером — для окна выбора папки у удалённого сервера.</summary>
public sealed class FolderBrowserTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-fb-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Lists_SubfoldersSorted_WithoutFilesAndHidden()
    {
        Directory.CreateDirectory(Path.Combine(_root, "beta"));
        Directory.CreateDirectory(Path.Combine(_root, "Alpha"));
        Directory.CreateDirectory(Path.Combine(_root, "Бэкапы"));
        File.WriteAllText(Path.Combine(_root, "file.txt"), "x");
        var hidden = Directory.CreateDirectory(Path.Combine(_root, ".secret"));
        if (OperatingSystem.IsWindows()) hidden.Attributes |= FileAttributes.Hidden;

        var listing = FolderBrowser.List(_root, "/nowhere");

        Assert.Equal(Path.GetFullPath(_root), listing.Path);
        Assert.Equal(3, listing.Dirs.Count);
        Assert.Contains("Бэкапы", listing.Dirs);
        var dirs = listing.Dirs.ToList();
        Assert.True(dirs.IndexOf("Alpha") < dirs.IndexOf("beta")); // по алфавиту, без учёта регистра
        Assert.Equal(Directory.GetParent(_root)!.FullName, listing.Parent);
        Assert.NotEmpty(listing.Roots);
        Assert.False(listing.Denied);
    }

    [Fact]
    public void MissingFolder_ShowsTheNearestOneAbove()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        var listing = FolderBrowser.List(Path.Combine(_root, "a", "b", "c"), _root);
        Assert.Equal(Path.Combine(_root, "a"), listing.Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/dir")]
    public void EmptyOrRelative_StartsFromTheStartFolder(string path)
    {
        var listing = FolderBrowser.List(path, _root);
        Assert.Equal(Path.GetFullPath(_root), listing.Path);
    }

    [Fact]
    public void Roots_OnLinuxAreTheRoot_OnWindowsTheDrives()
    {
        var roots = FolderBrowser.Roots();
        if (OperatingSystem.IsWindows()) Assert.All(roots, r => Assert.Matches(@"^[A-Z]:\\$", r));
        else Assert.Equal(["/"], roots);
    }
}

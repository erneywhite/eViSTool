using eViSTool.Core.Platform;

namespace eViSTool.Core.Tests;

public sealed class OwnerOnlyFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-owner-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ReplacesAWorldReadableFile_WithAnOwnerOnlyOne_AndLeavesNoTemp()
    {
        var path = Path.Combine(_dir, "remote.json");
        File.WriteAllText(path, "old");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        OwnerOnlyFile.WriteText(path, "секрет");

        Assert.Equal("секрет", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_dir)); // временный файл рядом не остался
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}

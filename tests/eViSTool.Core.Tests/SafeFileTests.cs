using eViSTool.Core.Platform;

namespace eViSTool.Core.Tests;

public sealed class SafeFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evistool-safefile-" + Guid.NewGuid().ToString("N"));

    public SafeFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Replace_WaitsOutAReader_InsteadOfFailing()
    {
        // на Windows подмена падала, пока файл держали открытым на чтение
        var file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(file, "old");
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, "new");

        var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () => { await Task.Delay(100); reader.Dispose(); });
        SafeFile.Replace(tmp, file);
        release.Wait();

        Assert.Equal("new", File.ReadAllText(file));
        Assert.False(File.Exists(tmp));
    }

    [Fact]
    public void Replace_GivesUp_WhenTheFileStaysLocked()
    {
        if (!OperatingSystem.IsWindows()) return; // на Linux открытый файл подмене не мешает
        var file = Path.Combine(_dir, "locked.json");
        File.WriteAllText(file, "old");
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, "new");

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<Exception>(() => SafeFile.Replace(tmp, file));
        Assert.Equal("old", File.ReadAllText(file));
    }
}

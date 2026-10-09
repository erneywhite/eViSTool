namespace eViSTool.Core.Tests;

/// <summary>Где лежат данные eViSTool: рядом с программой, а если туда нельзя — в запасной папке.</summary>
public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "evistool-paths-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [Fact]
    public void Data_LiesNextToTheProgram_OnLinuxOnlyForItsOwner()
    {
        var app = Directory.CreateDirectory(Path.Combine(_root, "evistool")).FullName;

        var root = AppPaths.Resolve(app, Path.Combine(_root, "fallback"));
        Assert.Equal(Path.Combine(app, "data"), root);
        Assert.True(Directory.Exists(root));
        Assert.False(Directory.Exists(Path.Combine(_root, "fallback")));
        // в папке данных ключи агента и секреты: другие пользователи машины туда не заглянут
        if (!OperatingSystem.IsWindows()) Assert.Equal(OwnerOnly, File.GetUnixFileMode(root));
    }

    [Fact]
    public void NextToTheProgramIsNotWritable_DataGoesToTheFallback()
    {
        // «папка программы» — файл: data рядом не создать никому, даже root
        var app = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(app, "");
        var fallback = Path.Combine(_root, "home", ".local", "share", "eViSTool");

        Assert.Equal(fallback, AppPaths.Resolve(app, fallback));
        Assert.True(Directory.Exists(fallback));
        if (!OperatingSystem.IsWindows()) Assert.Equal(OwnerOnly, File.GetUnixFileMode(fallback));
    }

    [Fact]
    public void FallbackRoot_IsAFullPath_EvenWithoutTheUsualFolders()
    {
        // у системного пользователя (vintagestory) ~/.local/share обычно нет — путь всё равно полный, не «eViSTool»
        Assert.True(Path.IsPathRooted(AppPaths.FallbackRoot), AppPaths.FallbackRoot);
        Assert.Equal("eViSTool", Path.GetFileName(AppPaths.FallbackRoot));
    }
}

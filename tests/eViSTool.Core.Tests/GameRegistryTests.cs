using eViSTool.Core.Game;

namespace eViSTool.Core.Tests;

/// <summary>Папка игры по записям «Установка и удаление программ» Windows (их пишет установщик Vintage Story).</summary>
public sealed class GameRegistryTests
{
    [Fact]
    public void InstallLocation_OfTheVintageStoryEntry_IsTheGameDir()
    {
        // так пишет установщик 1.22.7 (HKCU\…\Uninstall\{70364653-036D-49B3-8B80-AF39665F29C1}_is1)
        UninstallEntry[] entries =
        [
            new("7-Zip 24.08 (x64)", @"C:\Program Files\7-Zip\", @"C:\Program Files\7-Zip\7zFM.exe"),
            new("Vintage Story version 1.22.7", @"S:\Games\Vintagestory\", @"S:\Games\Vintagestory\VintageStory.exe"),
        ];
        Assert.Equal([@"S:\Games\Vintagestory"], GameInstall.GameDirsFromUninstallEntries(entries));
    }

    [Fact]
    public void WithoutInstallLocation_TheIconFolderIsTaken()
    {
        UninstallEntry[] entries =
        [
            new("Vintage Story", null, "\"D:\\VS\\VintageStory.exe\",0"),
            new("Vintage Story Server", "", @"E:\Server\VintagestoryServer.exe,0"),
            new("vintage story", "  ", "F:/vs/Vintagestory.exe"),
        ];
        Assert.Equal([@"D:\VS", @"E:\Server", "F:/vs"], GameInstall.GameDirsFromUninstallEntries(entries));
    }

    [Fact]
    public void OtherPrograms_EmptyEntries_AndRepeats_GiveNothingExtra()
    {
        UninstallEntry[] entries =
        [
            new(null, @"C:\X", null),
            new("Story of Seasons", @"C:\Games\SoS", null),
            new("Vintage Story version 1.22.7", null, null),
            new("Vintage Story version 1.22.7", @"""S:\Games\Vintagestory""", null),
            new("Vintage Story version 1.22.6", @"s:\games\vintagestory\", null),
        ];
        Assert.Equal([@"S:\Games\Vintagestory"], GameInstall.GameDirsFromUninstallEntries(entries));
    }

    [Fact]
    public void OnOtherSystems_TheRegistryIsNotRead()
    {
        if (OperatingSystem.IsWindows()) return; // на Windows — настоящий реестр машины: проверяем только, что не падает
        Assert.Empty(GameInstall.RegistryInstallDirs());
    }

    [Fact]
    public void ReadingTheRealRegistry_DoesNotThrow()
    {
        // на машине разработчика игра может стоять, а может и нет — важно, что чтение реестра не роняет поиск
        var dirs = GameInstall.RegistryInstallDirs().ToList();
        Assert.All(dirs, d => Assert.False(string.IsNullOrWhiteSpace(d)));
    }
}

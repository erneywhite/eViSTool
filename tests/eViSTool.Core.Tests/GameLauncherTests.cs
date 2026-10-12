using eViSTool.Core.Game;
using eViSTool.Core.Profiles;

namespace eViSTool.Core.Tests;

public sealed class GameLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _game;

    public GameLauncherTests()
    {
        _game = Path.Combine(_root, "Vintage Story");
        Directory.CreateDirectory(_game);
        File.WriteAllText(Path.Combine(_game, GameLauncher.ClientExeName), "");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private GameProfile Client(string? dataDir) => new() { Name = "Тест", Kind = ProfileKind.Client, GameDir = _game, DataDir = dataDir };

    [Fact]
    public void OwnDataDir_IsPassedAsDataPath()
    {
        var data = Path.Combine(_root, "Мои данные", "Профиль 2");
        var psi = GameLauncher.StartInfo(Client(data + Path.DirectorySeparatorChar));

        Assert.Equal(Path.Combine(_game, GameLauncher.ClientExeName), psi.FileName);
        Assert.Equal(_game, psi.WorkingDirectory);
        Assert.Equal(["--dataPath", data], psi.ArgumentList); // пробелы и кириллица — как есть, хвостовой разделитель убран
    }

    [Fact]
    public void DefaultDataDir_StartsGameWithoutArguments()
    {
        Assert.Empty(GameLauncher.StartInfo(Client(GameInstall.DefaultDataDir)).ArgumentList);
        Assert.Empty(GameLauncher.StartInfo(Client(GameInstall.DefaultDataDir.ToUpperInvariant() + "\\")).ArgumentList);
        Assert.Empty(GameLauncher.StartInfo(Client(null)).ArgumentList);
    }

    [Fact]
    public void Linux_PointsFontconfigToGameFonts_LikeRunSh()
    {
        if (!OperatingSystem.IsLinux()) return; // fontconfig — только на Linux
        Assert.False(GameLauncher.StartInfo(Client(null)).Environment.ContainsKey("FONTCONFIG_FILE")); // нет fonts.conf — не трогаем

        var fonts = Path.Combine(_game, "fonts.conf");
        File.WriteAllText(fonts, "");
        Assert.Equal(fonts, GameLauncher.StartInfo(Client(null)).Environment["FONTCONFIG_FILE"]);
    }

    [Fact]
    public void Flatpak_RunsThroughFlatpak_AndOpensOwnDataDirToTheSandbox()
    {
        if (!OperatingSystem.IsLinux()) return; // Flatpak — только на Linux
        var game = Path.Combine(_root, "flatpak", "app", GameInstall.FlatpakAppId, "current", "active", "files", "extra", "vintagestory");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, GameLauncher.ClientExeName), "");
        Assert.True(GameInstall.IsFlatpak(game));
        Assert.False(GameInstall.IsFlatpak(_game));

        // своя папка Flatpak — без параметров, как запускает его ярлык
        var own = new GameProfile { Name = "F", Kind = ProfileKind.Client, GameDir = game, DataDir = GameInstall.FlatpakDataDir };
        var psi = GameLauncher.StartInfo(own);
        Assert.Equal("flatpak", psi.FileName);
        Assert.Equal(["run", GameInstall.FlatpakAppId], psi.ArgumentList);

        // профиль со своими данными: песочнице открываем эту папку и передаём её игре
        var data = Path.Combine(_root, "Профиль 2");
        psi = GameLauncher.StartInfo(new GameProfile { Name = "F2", Kind = ProfileKind.Client, GameDir = game, DataDir = data });
        Assert.Equal(["run", "--filesystem=" + data, GameInstall.FlatpakAppId, "--dataPath", data], psi.ArgumentList);
    }

    [Fact]
    public void Refuses_ServerProfile_AndMissingGame()
    {
        var server = Client(null);
        server.Kind = ProfileKind.Server;
        Assert.Throws<InvalidOperationException>(() => GameLauncher.StartInfo(server));

        var noGame = Client(null);
        noGame.GameDir = Path.Combine(_root, "нет такой");
        Assert.Throws<FileNotFoundException>(() => GameLauncher.StartInfo(noGame));
        noGame.GameDir = null;
        Assert.Throws<FileNotFoundException>(() => GameLauncher.StartInfo(noGame));
    }
}

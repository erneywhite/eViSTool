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

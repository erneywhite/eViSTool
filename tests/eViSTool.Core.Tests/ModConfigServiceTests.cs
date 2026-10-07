using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.Core.Tests;

public sealed class ModConfigServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-mcs-" + Guid.NewGuid().ToString("N"));
    private readonly string _cfg;
    private readonly ModConfigService _service;

    public ModConfigServiceTests()
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        _cfg = Directory.CreateDirectory(ModConfigs.DirFor(data)).FullName;
        File.WriteAllText(Path.Combine(_root, "data", "secret.json"), "{}"); // рядом с ModConfig, но не в нём
        _service = new ModConfigService(data, new ModConfigBackups(Path.Combine(_root, "backups")), () => Array.Empty<LocalMod>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("../secret.json")]
    [InlineData("..\\secret.json")]
    [InlineData("sub/../../secret.json")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("icon.png")]          // не текстовый файл
    public void PathsOutsideModConfig_AreRefused(string path)
    {
        Assert.Throws<InvalidOperationException>(() => _service.Read(path));
        Assert.Throws<InvalidOperationException>(() => _service.Save(new ModConfigSaveRequest(path, "{}", null)));
    }

    [Fact]
    public void Save_RefusesIfTheFileChangedSinceItWasOpened()
    {
        File.WriteAllText(Path.Combine(_cfg, "a.json"), """{ "v": 1 }""");
        var opened = _service.Read("a.json");

        // тем временем файл переписали (другое окно или сам мод)
        File.WriteAllText(Path.Combine(_cfg, "a.json"), """{ "v": 9 }""");
        File.SetLastWriteTimeUtc(Path.Combine(_cfg, "a.json"), DateTime.UtcNow.AddMinutes(1));

        var result = _service.Save(new ModConfigSaveRequest("a.json", """{ "v": 2 }""", opened.ChangedUtc));
        Assert.True(result.Changed);
        Assert.Equal("""{ "v": 9 }""", File.ReadAllText(Path.Combine(_cfg, "a.json")));

        // «перезаписать» — без отметки времени
        result = _service.Save(new ModConfigSaveRequest("a.json", """{ "v": 2 }""", null));
        Assert.False(result.Changed);
        Assert.Equal("""{ "v": 2 }""", result.Content!.Text);
        Assert.Equal(1, result.Content.Versions);
    }

    [Fact]
    public void ReadSaveUndoReset_ByRelativePath()
    {
        Directory.CreateDirectory(Path.Combine(_cfg, "sub"));
        File.WriteAllText(Path.Combine(_cfg, "sub", "b.json"), """{ "v": 1 }""");

        var c = _service.Read("sub/b.json");
        Assert.True(c.Exists);
        c = _service.Save(new ModConfigSaveRequest("sub/b.json", """{ "v": 2 }""", c.ChangedUtc)).Content!;
        Assert.Equal(1, c.Versions);

        c = _service.Reset("sub/b.json");
        Assert.False(c.Exists);
        Assert.Equal(2, c.Versions);

        c = _service.Undo("sub/b.json");
        Assert.Equal("""{ "v": 2 }""", c.Text);
        c = _service.Undo("sub/b.json");
        Assert.Equal("""{ "v": 1 }""", c.Text);
        Assert.Equal(0, c.Versions);

        Assert.Equal(["sub/b.json"], _service.List().Select(e => e.RelativePath));
    }
}

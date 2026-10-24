using eViSTool.Core.Server;
using eViSTool.Presentation.ViewModels;
using Newtonsoft.Json.Linq;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Выбор мира на вкладке «Мир» редактора конфига: миры из Saves сервера (своего и удалённого — через агента) и
/// «Создать новый мир…». Новый мир — путь к файлу, которого ещё нет, своё имя и пустой сид; прежний мир не трогается.
/// </summary>
public sealed class ConfigWorldPickerTests : IClassFixture<TestServers>
{
    private readonly TestServers _s;

    public ConfigWorldPickerTests(TestServers servers)
    {
        _s = servers;
        _s.Reset();
    }

    [Theory]
    [InlineData("local-a")]
    [InlineData("remote-a")]
    public void CreateNewWorld_PointsTheConfigAtANewFile_AndKeepsTheOldOne(string name) => Sta.Run(() =>
    {
        var s = name == "local-a" ? _s.LocalA : _s.RemoteA;
        var saves = Directory.CreateDirectory(Path.Combine(s.DataDir, "Saves")).FullName;
        var old = Path.Combine(saves, "survival.vcdbs");
        File.WriteAllText(old, "world");
        var config = JObject.Parse(File.ReadAllText(s.ConfigPath));
        config["WorldConfig"]!["SaveFileLocation"] = old;
        config["WorldConfig"]!["Seed"] = "12345";
        File.WriteAllText(s.ConfigPath, config.ToString());

        using var client = s.Code is { } code ? AgentClient.ForRemote(code) : null;
        var vm = new ServerConfigViewModel();
        vm.OnProfileSwitched(s.IsRemote ? null : s.DataDir, s.Name, null, s.IsRemote ? s.View : null, () => client);
        vm.SetServerRunning(false);
        vm.SetActive(true);
        Sta.WaitFor(() => vm.WorldsAvailable);

        // в списке — мир из конфига (выбран) и «Создать новый мир…»
        Assert.Equal(2, vm.WorldChoices.Count);
        Assert.Equal(old, vm.SelectedWorld!.Path);
        Assert.True(vm.WorldExists);

        vm.SelectedWorld = vm.WorldChoices.Single(c => c.IsCreate);
        Sta.WaitFor(() => vm.IsCreatingWorld);
        vm.NewWorldName = "survival"; // занято
        vm.CreateWorldCommand.Execute(null);
        Assert.NotEmpty(vm.WorldError);

        vm.NewWorldName = "world2";
        vm.CreateWorldCommand.Execute(null);
        Assert.False(vm.IsCreatingWorld);
        Assert.False(vm.WorldExists); // файла нового мира ещё нет — параметры создания снова в силе
        Assert.Equal(Core.Localization.Loc.T("srvcfg.worldPending", "world2"), vm.SelectedWorld!.Label);

        Sta.Wait(vm.SaveCommand.ExecuteAsync(null));
        var saved = JObject.Parse(File.ReadAllText(s.ConfigPath))["WorldConfig"]!;
        Assert.Equal(Path.Combine(saves, "world2.vcdbs"), (string)saved["SaveFileLocation"]!);
        Assert.Equal("world2", (string)saved["WorldName"]!);
        Assert.Equal(JTokenType.Null, saved["Seed"]!.Type); // сид старого мира дал бы ту же карту
        Assert.Equal("world", File.ReadAllText(old)); // старый мир на месте

        // и назад на старый — просто выбором
        vm.SelectedWorld = vm.WorldChoices.Single(c => c.Path == old);
        Assert.True(vm.WorldExists);
    });

    /// <summary>
    /// Задумали новый мир (имя, сид), но вернулись к существующему: параметры создания — как были в конфиге, а не
    /// нового мира, и в списке выбран существующий (не пустой выбор).
    /// </summary>
    [Fact]
    public void BackToAnExistingWorld_RestoresTheCreationFields() => Sta.Run(() =>
    {
        var s = _s.LocalA;
        var saves = Directory.CreateDirectory(Path.Combine(s.DataDir, "Saves")).FullName;
        var old = Path.Combine(saves, "survival.vcdbs");
        File.WriteAllText(old, "world");
        var config = JObject.Parse(File.ReadAllText(s.ConfigPath));
        config["WorldConfig"]!["SaveFileLocation"] = old;
        config["WorldConfig"]!["WorldName"] = "Survival";
        config["WorldConfig"]!["Seed"] = "12345";
        File.WriteAllText(s.ConfigPath, config.ToString());

        var vm = new ServerConfigViewModel();
        vm.OnProfileSwitched(s.DataDir, s.Name);
        vm.SetServerRunning(false);
        vm.SetActive(true);
        Sta.WaitFor(() => vm.WorldsAvailable);

        vm.SelectedWorld = vm.WorldChoices.Single(c => c.IsCreate);
        Sta.WaitFor(() => vm.IsCreatingWorld);
        vm.NewWorldName = "123qwe";
        vm.CreateWorldCommand.Execute(null);
        string Text(string path) => vm.WorldGroups.SelectMany(g => g.Fields).OfType<ConfigTextFieldViewModel>().Single(f => f.Path == path).Text;
        Assert.Equal("123qwe", Text("WorldConfig.WorldName"));

        vm.SelectedWorld = vm.WorldChoices.Single(c => c.Path == old);
        Sta.WaitFor(() => vm.WorldChoices.All(c => !c.Label.Contains("123qwe"))); // список перестроен после выбора

        Assert.Equal("Survival", Text("WorldConfig.WorldName"));
        Assert.Equal("12345", Text("WorldConfig.Seed"));
        Assert.Equal(old, vm.SelectedWorld?.Path);
        Assert.False(vm.IsDirty); // всё как было загружено
    });
}

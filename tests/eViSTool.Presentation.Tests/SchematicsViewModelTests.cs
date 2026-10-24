using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Карточка «Постройки»: схема копируется в другой профиль через «Копировать в…», одноимённая там — с вопросом, а
/// после копирования команда «/we import имя» копируется в буфер обмена одним кликом.
/// </summary>
public sealed class SchematicsViewModelTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-schemvm-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        Ui.Platform = new ScriptedPlatform(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class Clipboard : TestUiPlatform
    {
        public string? Text { get; private set; }
        public override void SetClipboard(string text) => Text = text;
    }

    private GameProfile Profile(string id, string name, ProfileKind kind) => new()
    {
        Id = id, Name = name, Kind = kind, DataDir = Directory.CreateDirectory(Path.Combine(_root, id)).FullName,
    };

    [Fact]
    public async Task Copy_ToAnotherProfile_ThenTheImportCommandGoesToTheClipboard()
    {
        var server = Profile("srv", "Сервер", ProfileKind.Server);
        var solo = Profile("solo", "Соло", ProfileKind.Client);
        Schematics.Write(server.DataDir!, "dom.json", "{}"u8.ToArray());
        var clipboard = new Clipboard();
        Ui.Platform = clipboard;

        var vm = new SchematicsViewModel(server, () => [server, solo]);
        await vm.LoadAsync();
        var row = Assert.Single(vm.Rows);
        Assert.Equal("dom", row.Name);
        var target = Assert.Single(row.Targets); // себя в списке нет
        Assert.Equal("Соло", target.Name);
        Assert.False(vm.HasImportCommand);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)target.Command).ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(solo.DataDir!, "WorldEdit", "dom.json")));
        Assert.Equal("/we import dom", vm.ImportCommand);

        var copying = vm.CopyImportCommandCommand.ExecuteAsync(null);
        Assert.Equal("/we import dom", clipboard.Text);
        Assert.True(vm.CommandCopied);
        await copying;
        Assert.False(vm.CommandCopied); // «Скопировано» — на пару секунд
    }

    /// <summary>Выбор файлов «пользователя» и что ему сказали.</summary>
    private sealed class Picker(params string[] files) : TestUiPlatform
    {
        public List<string> Warned { get; } = [];
        public override IReadOnlyList<string>? PickFiles(string title, IReadOnlyList<FileFilter> filters, bool multiple) => files;
        public override void Notify(string text, NoticeKind kind) => Warned.Add(text);
    }

    [Fact]
    public async Task AddFromFile_PutsSchematicsIntoTheProfile_AndSkipsWhatIsNotOne()
    {
        var solo = Profile("solo", "Соло", ProfileKind.Client);
        var downloads = Directory.CreateDirectory(Path.Combine(_root, "downloads")).FullName;
        File.WriteAllText(Path.Combine(downloads, "zamok.json"), "{\"SizeX\":3}");
        File.WriteAllText(Path.Combine(downloads, "notes.json"), "not json at all");
        var picker = new Picker(Path.Combine(downloads, "zamok.json"), Path.Combine(downloads, "notes.json"));
        Ui.Platform = picker;

        var vm = new SchematicsViewModel(solo, () => [solo]);
        await vm.LoadAsync();
        Assert.True(vm.CanAdd);
        await vm.AddFromFileCommand.ExecuteAsync(null);

        Assert.Equal(["zamok"], vm.Rows.Select(r => r.Name));
        Assert.Equal("{\"SizeX\":3}", File.ReadAllText(Path.Combine(solo.DataDir!, "WorldEdit", "zamok.json")));
        Assert.Contains(picker.Warned, w => w.Contains("notes.json"));
    }

    /// <summary>В схеме блоки мода, которого в целевом профиле нет: сначала вопрос со списком, отказались — не копируем.</summary>
    [Fact]
    public async Task Copy_ToAProfileWithoutTheMods_AsksFirst()
    {
        var server = Profile("srv", "Сервер", ProfileKind.Server);
        var solo = Profile("solo", "Соло", ProfileKind.Client);
        Schematics.Write(server.DataDir!, "dom.json",
            """{"SizeX":2,"SizeY":2,"SizeZ":2,"BlockCodes":{"1":"game:rock-andesite","2":"millwright:axle"}}"""u8.ToArray());
        using var dialogs = new ScriptedDialogs(Answer.No);

        var vm = new SchematicsViewModel(server, () => [server, solo]);
        await vm.LoadAsync();
        Assert.Equal("2×2×2", vm.Rows[0].Details.Split(' ')[0]);
        Assert.Contains("millwright", vm.Rows[0].ModsText);
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.Rows[0].Targets[0].Command).ExecuteAsync(null);

        Assert.Contains("millwright", Assert.Single(dialogs.Asked));
        Assert.False(File.Exists(Path.Combine(solo.DataDir!, "WorldEdit", "dom.json")));
    }

    [Fact]
    public async Task Copy_OverTheSameName_AsksFirst()
    {
        var server = Profile("srv", "Сервер", ProfileKind.Server);
        var solo = Profile("solo", "Соло", ProfileKind.Client);
        Schematics.Write(server.DataDir!, "dom.json", "{\"new\":1}"u8.ToArray());
        Schematics.Write(solo.DataDir!, "dom.json", "{\"old\":1}"u8.ToArray());
        using var dialogs = new ScriptedDialogs(Answer.No);

        var vm = new SchematicsViewModel(server, () => [server, solo]);
        await vm.LoadAsync();
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.Rows[0].Targets[0].Command).ExecuteAsync(null);

        Assert.Single(dialogs.Asked);
        Assert.Equal("{\"old\":1}", File.ReadAllText(Path.Combine(solo.DataDir!, "WorldEdit", "dom.json"))); // отказались — не тронута
        Assert.False(vm.HasImportCommand);
    }
}

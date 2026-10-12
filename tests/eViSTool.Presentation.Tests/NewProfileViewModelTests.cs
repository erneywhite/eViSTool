using System.Reflection;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server.Remote;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Окно «Новый профиль»: название и папка будущего профиля, существующие данные сервера (нашлись конфиги или нет),
/// ответ каждой кнопки — как у окна на WPF.
/// </summary>
public sealed class NewProfileViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-npvm-" + Guid.NewGuid().ToString("N"));
    /// <summary>Стандартная папка данных (подменяет %APPDATA%\VintagestoryData).</summary>
    private readonly string _home;
    private readonly IUiPlatform? _oldPlatform = Ui.IsSet ? Ui.Platform : null;
    private readonly IAppWindows? _oldWindows = AppWindows.IsSet ? AppWindows.Current : null;

    public NewProfileViewModelTests()
    {
        _home = Directory.CreateDirectory(Path.Combine(_root, "VintagestoryData")).FullName;
    }

    public void Dispose()
    {
        // вернуть, как было (null — не задано: следующий тест поставит своё)
        Ui.Platform = _oldPlatform!;
        AppWindows.Current = _oldWindows!;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Папка с данными сервера: конфиги (файл и имя мира; мир null — конфиг без WorldConfig).</summary>
    private string ServerData(string name, params (string File, string? World)[] configs)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        foreach (var (file, world) in configs)
            File.WriteAllText(Path.Combine(dir, file), world is null ? "{}" : $$"""{ "WorldConfig": { "WorldName": "{{world}}" } }""");
        return dir;
    }

    private NewProfileViewModel Server(string name = "Сервер 2") => new(ProfileKind.Server, name, _home);

    // ---- открытие

    [Fact]
    public void Server_Opens_WithSuggestedName_FolderAndNoExistingData()
    {
        var vm = Server();

        Assert.True(vm.IsServer);
        Assert.Equal(Loc.T("newprofile.titleServer"), vm.Title);
        Assert.Equal("Сервер 2", vm.Name);
        Assert.Equal(Path.Combine(_home, ServerProfileLayout.ContainerName, "Сервер 2"), vm.FolderText);
        Assert.True(vm.CanCreate);
        Assert.True(vm.OkCommand.CanExecute(null));
        // стандартная папка без конфигов сервера — не подставляется
        Assert.Equal("", vm.ExistingDir);
        Assert.Equal("", vm.ExistingInfo);
        Assert.False(vm.ExistingInfoIsError);
        Assert.False(vm.CanUseExisting);
        Assert.False(vm.ImportCommand.CanExecute(null));
        Assert.False(vm.UseInPlaceCommand.CanExecute(null));
    }

    [Fact]
    public void Server_StandardFolderWithServerConfig_IsOfferedAsExistingData()
    {
        File.WriteAllText(Path.Combine(_home, "serverconfig.json"), """{ "WorldConfig": { "WorldName": "Дом" } }""");

        var vm = Server();

        Assert.Equal(_home, vm.ExistingDir);
        Assert.Equal(Loc.T("newprofile.existingFound", "serverconfig.json (Дом)"), vm.ExistingInfo);
        Assert.True(vm.CanUseExisting);
    }

    [Fact]
    public void Server_StandardFolderOfClient_IsNotOffered()
    {
        // папка клиента (есть clientsettings.json), даже с конфигом сервера рядом, — не данные сервера
        File.WriteAllText(Path.Combine(_home, "serverconfig.json"), "{}");
        File.WriteAllText(Path.Combine(_home, "clientsettings.json"), "{}");

        Assert.Equal("", Server().ExistingDir);
    }

    [Fact]
    public void Client_Opens_WithoutFolderAndExistingData()
    {
        File.WriteAllText(Path.Combine(_home, "serverconfig.json"), "{}"); // для клиента не смотрим

        var vm = new NewProfileViewModel(ProfileKind.Client, "Клиент 2", _home);

        Assert.False(vm.IsServer);
        Assert.Equal(Loc.T("newprofile.titleClient"), vm.Title);
        Assert.Equal("", vm.FolderText);
        Assert.Equal("", vm.ExistingDir);
        Assert.Equal("", vm.ExistingInfo);
        Assert.True(vm.CanCreate);
    }

    // ---- название

    [Fact]
    public void EmptyName_DisablesEverything_AndClearsFolder()
    {
        var vm = Server();
        vm.ExistingDir = ServerData("old", ("serverconfig.json", "Мир"));
        Assert.True(vm.CanUseExisting);

        vm.Name = "   ";

        Assert.False(vm.CanCreate);
        Assert.False(vm.OkCommand.CanExecute(null));
        Assert.Equal("", vm.FolderText);
        // кнопки существующих данных зависят и от названия
        Assert.False(vm.CanUseExisting);
        Assert.False(vm.ImportCommand.CanExecute(null));
        // а найденное в папке видно по-прежнему
        Assert.Equal(Loc.T("newprofile.existingFound", "serverconfig.json (Мир)"), vm.ExistingInfo);

        vm.Name = "Новый";

        Assert.True(vm.CanCreate);
        Assert.True(vm.CanUseExisting);
        Assert.Equal(Path.Combine(_home, ServerProfileLayout.ContainerName, "Новый"), vm.FolderText);
    }

    [Fact]
    public void Name_IsTrimmed_InFolder()
    {
        var vm = Server("  Выживание  ");

        Assert.Equal(Path.Combine(_home, ServerProfileLayout.ContainerName, "Выживание"), vm.FolderText);
    }

    [Fact]
    public void FolderTaken_GetsNextNumber()
    {
        // папка с таким именем уже есть и не пуста — профилю достанется «имя-2»
        var taken = Directory.CreateDirectory(Path.Combine(_home, ServerProfileLayout.ContainerName, "Сервер 2")).FullName;
        File.WriteAllText(Path.Combine(taken, "serverconfig.json"), "{}");

        var vm = Server();

        Assert.Equal(taken + "-2", vm.FolderText);
    }

    // ---- существующие данные

    [Fact]
    public void ExistingFolder_WithConfigs_ListsThemWithWorlds()
    {
        var vm = Server();

        vm.ExistingDir = "  " + ServerData("old", ("serverconfig-b.json", null), ("serverconfig.json", "Дом"), ("serverconfig-a.json", "")) + "  ";

        // основной конфиг первым, остальные по имени; мир — в скобках, если есть
        Assert.Equal(Loc.T("newprofile.existingFound", "serverconfig.json (Дом), serverconfig-a.json, serverconfig-b.json"), vm.ExistingInfo);
        Assert.False(vm.ExistingInfoIsError);
        Assert.True(vm.CanUseExisting);
        Assert.True(vm.ImportCommand.CanExecute(null));
        Assert.True(vm.UseInPlaceCommand.CanExecute(null));
    }

    [Fact]
    public void ExistingFolder_WithoutConfigs_IsAnError()
    {
        var vm = Server();

        vm.ExistingDir = ServerData("empty");

        Assert.Equal(Loc.T("newprofile.existingNone"), vm.ExistingInfo);
        Assert.True(vm.ExistingInfoIsError);
        Assert.False(vm.CanUseExisting);
    }

    [Fact]
    public void ExistingFolder_Missing_IsAnError_AndEmptyClearsTheText()
    {
        var vm = Server();

        vm.ExistingDir = Path.Combine(_root, "nowhere");
        Assert.Equal(Loc.T("newprofile.existingNone"), vm.ExistingInfo);
        Assert.True(vm.ExistingInfoIsError);

        vm.ExistingDir = " ";
        Assert.Equal("", vm.ExistingInfo);
        Assert.False(vm.ExistingInfoIsError);
        Assert.False(vm.CanUseExisting);
    }

    [Fact]
    public void Browse_PutsPickedFolder_CancelKeepsTheOld()
    {
        var picked = ServerData("picked", ("serverconfig.json", "Мир"));
        var asked = new List<object?[]>();
        string? answer = picked;
        Ui.Platform = Fake<IUiPlatform>.Of(("PickFolder", args => { asked.Add(args); return answer; }));
        var vm = Server();
        vm.ExistingDir = " C:\\start ";

        vm.BrowseCommand.Execute(null);

        var call = Assert.Single(asked);
        Assert.Equal(Loc.T("newprofile.existingPick"), call[0]);
        Assert.Equal("C:\\start", call[1]); // начать с того, что в поле
        Assert.Equal(picked, vm.ExistingDir);
        Assert.True(vm.CanUseExisting);

        answer = null; // отменили выбор
        vm.BrowseCommand.Execute(null);
        Assert.Equal(picked, vm.ExistingDir);
    }

    // ---- ответы

    [Fact]
    public void Ok_Server_CreatesFolderNamedAfterProfile()
    {
        var vm = Server(" Сервер 2 ");
        NewProfileChoice? done = null;
        vm.Finished += c => done = c;

        vm.OkCommand.Execute(null);

        Assert.NotNull(done);
        Assert.Same(done, vm.Result);
        Assert.Equal(new NewProfileChoice("Сервер 2", Path.Combine(_home, ServerProfileLayout.ContainerName, "Сервер 2"), ExistingProfileMode.None, null, null), done);
    }

    [Fact]
    public void Ok_Client_HasNoDataDir()
    {
        var vm = new NewProfileViewModel(ProfileKind.Client, "Клиент 2", _home);

        vm.OkCommand.Execute(null);

        Assert.Equal(new NewProfileChoice("Клиент 2", null, ExistingProfileMode.None, null, null), vm.Result);
    }

    [Fact]
    public void Ok_EmptyName_DoesNothing()
    {
        var vm = Server("");
        var finished = false;
        vm.Finished += _ => finished = true;

        vm.OkCommand.Execute(null);

        Assert.False(finished);
        Assert.Null(vm.Result);
    }

    [Theory]
    [InlineData(ExistingProfileMode.Import)]
    [InlineData(ExistingProfileMode.UseInPlace)]
    public void Existing_AnswersWithTheFolder(ExistingProfileMode mode)
    {
        var dir = ServerData("old", ("serverconfig.json", "Мир"));
        var vm = Server(" Старый ");
        vm.ExistingDir = dir + " ";

        (mode == ExistingProfileMode.Import ? vm.ImportCommand : vm.UseInPlaceCommand).Execute(null);

        // данные — прямо в той папке (для Import — откуда копировать)
        Assert.Equal(new NewProfileChoice("Старый", dir, mode, null, dir), vm.Result);
    }

    [Fact]
    public void Existing_EmptyName_DoesNothing()
    {
        var vm = Server("");
        vm.ExistingDir = ServerData("old", ("serverconfig.json", "Мир"));

        vm.ImportCommand.Execute(null);
        vm.UseInPlaceCommand.Execute(null);

        Assert.Null(vm.Result);
    }

    [Fact]
    public void Connect_WithCode_AnswersRemote_WithEmptyDataDir()
    {
        var code = new ConnectionCode("10.0.0.5", 42420, "key", "fingerprint");
        var asked = new List<object?[]>();
        AppWindows.Current = Fake<IAppWindows>.Of(("AskConnectionCode", args => { asked.Add(args); return code; }));
        var vm = Server(" Дальний ");

        vm.ConnectCommand.Execute(null);

        Assert.Equal(Loc.T("newprofile.connectOk"), Assert.Single(asked)[0]);
        // DataDir — пустая строка, не null: настройки по ней отличают «выбрали» от «отменили»
        Assert.Equal(new NewProfileChoice("Дальний", "", ExistingProfileMode.Remote, code, null), vm.Result);
    }

    [Fact]
    public void Connect_Cancelled_DoesNothing()
    {
        AppWindows.Current = Fake<IAppWindows>.Of(("AskConnectionCode", _ => null));
        var vm = Server();

        vm.ConnectCommand.Execute(null);

        Assert.Null(vm.Result);
    }

    [Fact]
    public void Connect_EmptyName_FocusesTheName_WithoutAsking()
    {
        // спросить код — ошибка теста: без названия окно кода не открывается
        AppWindows.Current = Fake<IAppWindows>.Of();
        var vm = Server(" ");
        var focused = 0;
        vm.FocusNameRequested += () => focused++;

        vm.ConnectCommand.Execute(null);

        Assert.Equal(1, focused);
        Assert.Null(vm.Result);
    }

    /// <summary>
    /// Подделка интерфейса: отвечают только заданные методы (по имени), любой другой вызов роняет тест. Открытый
    /// (DispatchProxy строит наследника в своей сборке) и вложенный (имя не столкнётся с подделками других тестов).
    /// </summary>
    public class Fake<T> : DispatchProxy where T : class
    {
        private Dictionary<string, Func<object?[], object?>> _answers = [];

        public static T Of(params (string Method, Func<object?[], object?> Answer)[] answers)
        {
            var proxy = Create<T, Fake<T>>();
            ((Fake<T>)(object)proxy)._answers = answers.ToDictionary(a => a.Method, a => a.Answer);
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod is not null && _answers.TryGetValue(targetMethod.Name, out var answer)
                ? answer(args ?? [])
                : throw new InvalidOperationException($"Неожиданный вызов {typeof(T).Name}.{targetMethod?.Name}");
    }
}

using eViSTool.Core;
using eViSTool.Core.Localization;
using eViSTool.Core.Notifications;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Вкладка «Оповещения» раздела «Сервер»: переход в «Настройки» из подсказки «каналов ещё нет»; кнопка проверочного
/// сразу после щелчков; записи по одному и итог записи после смены профиля. Каналы и настройки своих серверов — в
/// папке data рядом с тестами; прежний файл каналов возвращается на место.
/// </summary>
public sealed class ServerNotifyViewModelTests : IDisposable
{
    private static readonly NotifyChannel Telegram = new() { Id = "tg", Name = "Telegram", Kind = NotifyKind.Telegram, ChatId = "1" };

    private readonly string _channelsFile = NotifyChannels.FileIn(AppPaths.Root);
    private readonly List<GameProfile> _servers = [];
    private byte[]? _oldChannels;
    private bool _channelsSaved;
    private int _saves;

    /// <summary>Данные рядом с тестами доступны для записи: иначе тест ничего не пишет — настоящие данные пользователя не трогаем.</summary>
    private static bool CanRun => AppPaths.IsPortable;

    public void Dispose()
    {
        foreach (var server in _servers) TryDelete(ServerNotifySettings.FileFor(server.Id));
        if (!_channelsSaved) return;
        if (_oldChannels is not null) File.WriteAllBytes(_channelsFile, _oldChannels);
        else TryDelete(_channelsFile);
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
    }

    /// <summary>Свой сервер (агент не запущен: настройки пишет окно) с одним каналом Telegram в «Настройках».</summary>
    private GameProfile Server(string name)
    {
        if (!_channelsSaved)
        {
            _oldChannels = File.Exists(_channelsFile) ? File.ReadAllBytes(_channelsFile) : null;
            _channelsSaved = true;
            NotifyChannels.Save(AppPaths.Root, [Telegram]);
        }
        var profile = new GameProfile { Id = "notify-" + name + "-" + Guid.NewGuid().ToString("N")[..6], Name = name, Kind = ProfileKind.Server };
        _servers.Add(profile);
        return profile;
    }

    /// <summary>Вкладка этого сервера, открытая и прочитанная.</summary>
    private ServerNotifyViewModel Tab(GameProfile profile)
    {
        var vm = new ServerNotifyViewModel(new ServerViewModel(null!));
        vm.Saved += () => _saves++;
        vm.OnProfileSwitched(profile);
        vm.SetActive(true);
        Sta.WaitFor(() => vm.Loaded);
        return vm;
    }

    private static NotifyCellViewModel Cell(ServerNotifyViewModel vm, NotifyEvent e) => vm.Rows.Single(r => r.Event == e).Cells.Single();

    /// <summary>Покрутить очередь окна ещё немного: записи, которых не должно быть, успели бы прийти.</summary>
    private static void Settle()
    {
        for (var i = 0; i < 20; i++)
        {
            UiThread.Current!.RunPending();
            Thread.Sleep(10);
        }
    }

    // «Открыть настройки» ведёт в общие «Настройки», где каналы, а не в «Профили», куда ведут остальные переходы раздела.
    // Главная модель настоящая: ей принадлежит выбранная вкладка. Данные она берёт из папки data рядом с тестами.
    [Fact]
    public void OpenSettings_SwitchesMainWindowToSettings() => Sta.Run(() =>
    {
        var language = Loc.Instance.Language; // главная модель выставляет язык из настроек — другим тестам его не менять
        try
        {
            var main = new MainViewModel { SelectedTab = AppTab.Server };

            main.Server.Notify.OpenSettingsCommand.Execute(null);

            Assert.Equal(AppTab.Settings, main.SelectedTab);
        }
        finally { Loc.Instance.SetLanguage(language); }
    });

    // команда доступна всегда: без каналов она единственный путь к их добавлению
    [Fact]
    public void OpenSettings_AlwaysCanExecute() => Sta.Run(() =>
    {
        var notify = new ServerNotifyViewModel(new ServerViewModel(null!));

        Assert.True(notify.OpenSettingsCommand.CanExecute(null));
    });

    // первый маршрут включили — кнопка проверочного оживает сразу, последний выключили — гаснет; раньше она ждала, пока
    // вкладку откроют заново (у хостера и у своего сервера без агента статус агента её не будит)
    [Fact]
    public void TestButton_FollowsTheRoutes_AsSoonAsTheyAreSaved() => Sta.Run(() =>
    {
        if (!CanRun) return;
        var vm = Tab(Server("a"));
        var command = vm.TestCommand;
        Assert.False(command.CanExecute(null)); // маршрутов нет
        var changed = 0;
        command.CanExecuteChanged += (_, _) => changed++;

        Cell(vm, NotifyEvent.ServerCrashed).IsOn = true;
        Sta.WaitFor(() => _saves == 1);
        Assert.NotEqual(0, changed);
        Assert.True(command.CanExecute(null));

        changed = 0;
        Cell(vm, NotifyEvent.ServerCrashed).IsOn = false;
        Sta.WaitFor(() => _saves == 2);
        Assert.NotEqual(0, changed);
        Assert.False(command.CanExecute(null));
    });

    // щелчки быстрее записи: файл пишется по одному разу за раз, а щелчки во время записи уходят следующей записью —
    // вся таблица разом. Раньше каждый щелчок писал свой файл .tmp параллельно с другими
    [Fact]
    public void QuickClicks_AreWrittenOneAtATime_AndTheLastStateWins() => Sta.Run(() =>
    {
        if (!CanRun) return;
        var server = Server("a");
        var vm = Tab(server);

        foreach (var row in vm.Rows) row.Cells.Single().IsOn = true; // очередь окна не крутится — первая запись ещё не кончилась
        Sta.WaitFor(() => _saves >= 2);
        Settle();

        Assert.Equal(2, _saves); // первый щелчок и одна запись на все остальные
        Assert.Equal("", vm.ErrorText);
        Assert.Equal(vm.Rows.Select(r => r.Event).Order(), ServerNotifySettings.Load(server.Id).Routes.Keys.Order());
        Assert.True(vm.TestCommand.CanExecute(null));
    });

    // запись кончилась, когда вкладка уже показывает другой сервер: щелчок записан туда, где его сделали, а настройки
    // вкладки (кнопка проверочного, пересборка при смене языка) и строка ошибки остались от открытого сервера
    [Fact]
    public void SaveThatEndsAfterAProfileSwitch_LeavesTheNewProfileAlone() => Sta.Run(() =>
    {
        if (!CanRun) return;
        var (a, b) = (Server("a"), Server("b"));
        var vm = Tab(a);

        Cell(vm, NotifyEvent.ServerCrashed).IsOn = true; // запись a пошла
        vm.OnProfileSwitched(b);                         // и сразу другой сервер, пока она не кончилась
        Sta.WaitFor(() => _saves == 1);
        Settle();

        Assert.True(ServerNotifySettings.Load(a.Id).IsOn(NotifyEvent.ServerCrashed, Telegram.Id));
        Assert.False(File.Exists(ServerNotifySettings.FileFor(b.Id))); // b никто не трогал
        Assert.True(vm.Loaded);
        Assert.False(vm.TestCommand.CanExecute(null)); // у b маршрутов нет
        Assert.Equal("", vm.ErrorText);
        vm.OnLanguageChanged(); // таблица пересобирается по настройкам вкладки — они от b
        Assert.All(vm.Rows, r => Assert.False(r.Cells.Single().IsOn));
    });
}

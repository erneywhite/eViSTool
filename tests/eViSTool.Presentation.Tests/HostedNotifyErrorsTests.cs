using eViSTool.Core;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Notifications;
using eViSTool.Core.Tests.Hosting;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Вкладка «Оповещения» сервера у хостера: оповещения шлёт окно (<see cref="HostedWatch"/>, отправка перехвачена), и
/// ошибка отправки видна, пока в этот канал не уйдёт удачное оповещение или проверочное, — в том числе после того, как
/// вкладку открыли заново или вернулись на профиль. Канал поправили в «Настройках» или выключили у сервера — ошибка
/// уходит. У своего сервера (шлёт агент) этих ошибок на вкладке нет. Каналы и настройки сервера — в папке data рядом с
/// тестами; прежний файл каналов возвращается на место.
/// </summary>
public sealed class HostedNotifyErrorsTests : IDisposable
{
    private static readonly NotifyChannel Telegram = new() { Id = "tg", Name = "Telegram", Kind = NotifyKind.Telegram, ChatId = "1" };

    private readonly HostedFixture _host = new();
    private readonly string _channelsFile = NotifyChannels.FileIn(AppPaths.Root);
    private readonly byte[]? _oldChannels;
    private readonly List<HostedWatch> _watches = [];
    private Func<NotifyChannel, Task> _send = _ => Task.CompletedTask;
    private int _saves;

    /// <summary>Данные рядом с тестами доступны для записи: иначе тест ничего не пишет — настоящие данные пользователя не трогаем.</summary>
    private static bool CanRun => AppPaths.IsPortable;

    public HostedNotifyErrorsTests()
    {
        if (!CanRun) return;
        _oldChannels = File.Exists(_channelsFile) ? File.ReadAllBytes(_channelsFile) : null;
        NotifyChannels.Save(AppPaths.Root, [Telegram]);
        new ServerNotifySettings { Channels = [Telegram], Routes = new() { [NotifyEvent.ModsUpdated] = ["tg"] } }.Save(_host.Profile.Id);
    }

    public void Dispose()
    {
        foreach (var watch in _watches) watch.Stop();
        if (CanRun)
        {
            if (_oldChannels is not null) File.WriteAllBytes(_channelsFile, _oldChannels);
            else TryDelete(_channelsFile);
            TryDelete(ServerNotifySettings.FileFor(_host.Profile.Id));
        }
        _host.Dispose();
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
    }

    // ---------- стенд

    private HostedWatch Watch()
    {
        var updates = new HostedModUpdates((_, _) => Task.FromResult<IReadOnlyList<ModCheckResult>>([]));
        var watch = new HostedWatch([new ProfileViewModel(_host.Profile, _ => { })], updates) { Send = (channel, _) => _send(channel) };
        _watches.Add(watch);
        return watch;
    }

    /// <summary>Вкладка хостового профиля, открытая и прочитанная (с обновлением копий каналов у сервера).</summary>
    private ServerNotifyViewModel Tab(HostedWatch watch)
    {
        var vm = new ServerNotifyViewModel(new ServerViewModel(null!), hosted: true) { Watch = watch };
        vm.Saved += () => _saves++;
        vm.OnProfileSwitched(_host.Profile);
        Open(vm);
        return vm;
    }

    /// <summary>Открыть вкладку и дождаться, пока она прочитала настройки и обновила копии каналов у сервера.</summary>
    private void Open(ServerNotifyViewModel vm)
    {
        var saves = _saves;
        vm.SetActive(true);
        Sta.WaitFor(() => vm.Loaded && _saves > saves);
    }

    private void Reopen(ServerNotifyViewModel vm)
    {
        vm.SetActive(false);
        Open(vm);
    }

    /// <summary>Окно поставило мод на сервер — оповещение «Обновлены моды»; дождаться отправки и её итога в потоке окна.</summary>
    private void ModsUpdated(HostedWatch watch)
    {
        watch.ModsChanged(_host.Target, ["alpha 1.1.0"], []);
        watch.ModsChangeDone();
        Sta.Wait(watch.SentAsync(_host.Profile.Id));
        UiThread.Current!.RunPending();
    }

    // ---------- ошибка держится

    [Fact]
    public void SendError_StaysWhenTheTabIsReopened_OrTheProfileSwitched_UntilASendToThatChannelGoesThrough() => Sta.Run(() =>
    {
        if (!CanRun) return;
        var watch = Watch();
        var vm = Tab(watch);
        var own = new ServerNotifyViewModel(new ServerViewModel(null!)) { Watch = watch }; // вкладка своего сервера
        own.OnProfileSwitched(_host.Profile);
        _send = _ => throw new NotifyException("Unauthorized");

        ModsUpdated(watch);

        var error = Loc.T("notify.sendFailed", "Telegram", "Unauthorized");
        Assert.Equal(error, vm.ErrorText);
        Assert.Equal("", own.ErrorText); // у своего сервера шлёт агент — ошибки окна сюда не приходят
        Reopen(vm);
        Assert.Equal(error, vm.ErrorText); // раньше открытие вкладки стирало её

        using var other = new HostedFixture();
        vm.OnProfileSwitched(other.Profile);
        Assert.Equal("", vm.ErrorText); // у другого сервера ничего не случилось
        Sta.WaitFor(() => vm.Loaded);
        vm.OnProfileSwitched(_host.Profile);
        Assert.Equal(error, vm.ErrorText); // вернулись — она на месте, ещё до чтения настроек
        Sta.WaitFor(() => vm.Loaded);
        Assert.Equal(error, vm.ErrorText);

        // на другом языке — тоже: строка собирается при показе
        var language = Loc.Instance.Language;
        try
        {
            Loc.Instance.SetLanguage(language == "ru" ? "en" : "ru");
            vm.OnLanguageChanged();
            Assert.Equal(Loc.T("notify.sendFailed", "Telegram", "Unauthorized"), vm.ErrorText);
            Assert.NotEqual(error, vm.ErrorText);
        }
        finally
        {
            Loc.Instance.SetLanguage(language);
            vm.OnLanguageChanged();
        }

        _send = _ => Task.CompletedTask;
        ModsUpdated(watch);

        Assert.Equal("", vm.ErrorText);
        Assert.Empty(watch.SendFailures(_host.Profile.Id));
        Reopen(vm);
        Assert.Equal("", vm.ErrorText);
    });

    [Fact]
    public void TestMessage_ThatGoesThrough_ClearsTheChannelsError() => Sta.Run(() =>
    {
        if (!CanRun) return;
        var watch = Watch();
        var vm = Tab(watch);
        _send = _ => throw new NotifyException("Bad Gateway");
        ModsUpdated(watch);
        Assert.NotEqual("", vm.ErrorText);

        _send = _ => Task.CompletedTask;
        Assert.True(vm.TestCommand.CanExecute(null));
        Sta.Wait(vm.TestCommand.ExecuteAsync(null)); // проверочное — тем же путём, что настоящие
        UiThread.Current!.RunPending();               // итог по каналу приходит в поток окна

        Assert.Equal(Loc.T("srvnotify.testSent"), vm.StatusText);
        Assert.Equal("", vm.ErrorText);
        Assert.Empty(watch.SendFailures(_host.Profile.Id));
        Reopen(vm);
        Assert.Equal("", vm.ErrorText);
    });

    // ---------- ошибка уходит, когда она уже не про этот канал

    [Fact]
    public void SendError_GoesAway_WhenTheChannelIsFixedInSettings_OrTurnedOffForTheServer() => Sta.Run(() =>
    {
        if (!CanRun) return;
        var watch = Watch();
        var vm = Tab(watch);
        _send = _ => throw new NotifyException("chat not found");
        ModsUpdated(watch);
        Assert.Equal(Loc.T("notify.sendFailed", "Telegram", "chat not found"), vm.ErrorText);

        NotifyChannels.Save(AppPaths.Root, [Telegram with { ChatId = "2" }]); // поправили чат в «Настройках»
        Reopen(vm); // копия канала у сервера обновилась — прежняя ошибка была про старый чат

        Assert.Equal("", vm.ErrorText);
        Assert.Empty(watch.SendFailures(_host.Profile.Id));

        ModsUpdated(watch); // и с новым чатом не ушло — это уже новая ошибка
        Assert.Equal(Loc.T("notify.sendFailed", "Telegram", "chat not found"), vm.ErrorText);

        // выключили канал у сервера: «Обновлены моды» × Telegram — был его единственный маршрут
        var saves = _saves;
        vm.Rows.Single(r => r.Event == NotifyEvent.ModsUpdated).Cells.Single(c => c.ChannelId == "tg").IsOn = false;
        Sta.WaitFor(() => _saves > saves);

        Assert.Equal("", vm.ErrorText);
        Assert.Empty(watch.SendFailures(_host.Profile.Id));
    });

    // маршруты выключили в обход вкладки (другое окно, файл): сторож отпускает профиль, и строка ошибок открытой вкладки
    // не держит забытые им ошибки
    [Fact]
    public void SendError_GoesAwayFromTheOpenTab_WhenTheWatchReleasesTheProfile() => Sta.Run(() =>
    {
        if (!CanRun) return;
        var watch = Watch();
        var vm = Tab(watch);
        _send = _ => throw new NotifyException("Forbidden");
        ModsUpdated(watch);
        Assert.Equal(Loc.T("notify.sendFailed", "Telegram", "Forbidden"), vm.ErrorText);

        new ServerNotifySettings { Channels = [Telegram] }.Save(_host.Profile.Id);
        watch.Refresh();

        Assert.Empty(watch.SendFailures(_host.Profile.Id));
        Assert.Equal("", vm.ErrorText);
    });
}

using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Notifications;
using eViSTool.Core.Profiles;
using eViSTool.Core.Tests.Hosting;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// «Обновить всё перед запуском» в разделе «Сервер» у хостера: плашка «Обновления модов: N» — только когда сервер по логу
/// остановлен или вылетел и обновления известны (свежая проверка — не старше 30 минут, иначе проверка заново); кнопка —
/// та же операция, что «Обновить всё» в «Моих модах», без вопроса «останови сервер», а итог уходит оповещением
/// «Обновлены моды». Проверка обновлений подменена (модбаза не нужна), FTP — поддельный «как G-Portal».
/// </summary>
public sealed class HostedUpdatesPlateTests : IDisposable
{
    private const string Main = "/Logs/server-main.log";
    private readonly HostedFixture _host = new();
    private readonly TestTime _time = new(new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc));
    private IReadOnlyList<ModCheckResult> _due = [];
    private int _checks;

    public HostedUpdatesPlateTests() => _host.GPortalLayout(log: false);

    public void Dispose()
    {
        _host.Dispose();
        try { File.Delete(ServerNotifySettings.FileFor(_host.Profile.Id)); } catch (IOException) { }
    }

    private static readonly string[] FatalLines =
    [
        "17.10.2026 13:40:00 [Fatal] Game Version: v1.22.7 (Stable)",
        "Critical error occurred in the following mod: badmod@1.0.0",
    ];

    private static string[] StoppedTail => HostedLogSamples.Stopped[HostedLogSamples.Running.Length..];

    private string PutLog(string[] lines)
    {
        var marks = new HostedRunMarks();
        foreach (var line in lines) marks.Add(line);
        var local = _host.Server.Put(Main, HostedLogSamples.Bytes(lines));
        File.SetLastWriteTimeUtc(local, DateTime.SpecifyKind(marks.LastTime!.Value.AddHours(-2), DateTimeKind.Utc));
        return local;
    }

    private static void Append(string local, IEnumerable<string> lines)
    {
        using var file = new FileStream(local, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        file.Write(HostedLogSamples.Bytes(lines));
    }

    private HostedModUpdates Updates() => new((_, _) =>
    {
        _checks++;
        return Task.FromResult(_due);
    }, _time);

    private static ModCheckResult Update(string modId, string installed, string latest) => new()
    {
        Local = new LocalMod($"/Mods/{modId}_{installed}.zip", new ModInfo { ModId = modId, OriginalModId = modId, Name = modId, Version = installed }, null),
        Status = ModStatus.UpdateAvailable,
        LatestCompatible = new ModDbRelease { ModVersion = latest, MainFile = $"https://example.test/{modId}_{latest}.zip" },
    };

    private static void Poll(HostedServerViewModel vm)
    {
        Sta.Wait(vm.PollAsync());
        Sta.WaitFor(() => UiThread.Current?.RunPending() != true); // и проверка обновлений после опроса
    }

    [Fact]
    public void Plate_OnlyWhenTheServerIsDownByTheLog_AndUpdatesAreKnown() => Sta.Run(() =>
    {
        var local = PutLog(HostedLogSamples.Running);
        _due = [Update("alpha", "1.0.0", "1.1.0"), Update("beta", "2.0.0", "2.1.0")];
        var vm = new HostedServerViewModel(null!) { Updates = Updates() };
        vm.OnProfileSwitched(_host.Profile);

        Poll(vm);
        Assert.Equal(HostedServerState.Running, vm.State);
        Assert.False(vm.ShowUpdates);
        Assert.Equal(0, _checks);                     // сервер работает — и проверять незачем

        Append(local, StoppedTail);                   // остановили в панели хостера
        Poll(vm);
        Assert.Equal(HostedServerState.Stopped, vm.State);
        Assert.Equal(1, _checks);
        Assert.True(vm.ShowUpdates);
        Assert.Equal(Loc.T("server.hostedUpdates", 2), vm.UpdatesText);
        Assert.True(vm.UpdateAllCommand.CanExecute(null));

        Poll(vm);
        Assert.Equal(1, _checks);                     // свежая проверка — не повторяем на каждом опросе
        _time.UtcNow += HostedServerViewModel.UpdatesFreshFor + TimeSpan.FromSeconds(1);
        Poll(vm);
        Assert.Equal(2, _checks);                     // старше 30 минут — проверяем заново

        _due = [];
        _time.UtcNow += HostedServerViewModel.UpdatesFreshFor + TimeSpan.FromSeconds(1);
        Poll(vm);
        Assert.False(vm.ShowUpdates);                 // обновлений нет — плашки нет
    });

    [Fact]
    public void Plate_ShowsAfterACrash_AndGoesAwayWhenTheServerStartsAgain() => Sta.Run(() =>
    {
        PutLog([.. HostedLogSamples.Running, .. FatalLines]);
        AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of((nameof(IAppWindows.ShowServerCrash), _ => null));
        _due = [Update("alpha", "1.0.0", "1.1.0")];
        var vm = new HostedServerViewModel(null!) { Updates = Updates() };
        vm.OnProfileSwitched(_host.Profile);

        Poll(vm);
        Assert.Equal(HostedServerState.Crashed, vm.State);
        Assert.True(vm.ShowUpdates);

        // панель хостера запустила сервер заново
        _host.Server.Put("/Logs/Archive/2026-10-17_13_27_08/server-main.log", HostedLogSamples.Bytes([.. HostedLogSamples.Running, .. FatalLines]));
        PutLog([HostedLogSamples.Stopped[0].Replace("17.10.2026", "18.10.2026"),
            HostedLogSamples.Stopped.First(l => l.Contains("now running on Port", StringComparison.Ordinal)).Replace("17.10.2026", "18.10.2026")]);
        Poll(vm);
        Assert.Equal(HostedServerState.Running, vm.State);
        Assert.False(vm.ShowUpdates);
        Assert.False(vm.UpdateAllCommand.CanExecute(null));
    });

    [Fact]
    public void UpdateAll_IsTheSameOperationAsInMyMods_WithoutTheStopQuestion_AndNotifiesModsUpdated() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        // «Обновлены моды» включено у сервера — в папке агентов этой машины (данные рядом с тестами)
        new ServerNotifySettings
        {
            Channels = [new NotifyChannel { Id = "tg", Name = "Telegram", Kind = NotifyKind.Telegram }],
            Routes = new() { [NotifyEvent.ModsUpdated] = ["tg"] },
        }.Save(_host.Profile.Id);
        var language = Loc.Instance.Language; // главная модель выставляет язык из настроек — другим тестам его не менять
        var main = new MainViewModel { AutoCheckUpdates = false };
        Loc.Instance.SetLanguage(language);
        var sent = new List<NotifyMessage>();
        main.HostedWatch.Send = (_, message) =>
        {
            lock (sent) sent.Add(message);
            return Task.CompletedTask;
        };
        try
        {
            main.Profiles.Clear();
            var hosted = new ProfileViewModel(_host.Profile, _ => { });
            main.Profiles.Add(hosted);
            main.ActiveProfile = hosted;
            var plate = main.Server.Hosted;
            _due = [Update("alpha", "1.0.0", "1.1.0")];
            plate.Updates = Updates();
            // вместо скачивания с модбазы — архив на этой машине: остальное — как у настоящего «Обновить всё»
            var zip = _host.Zip("alpha_1.1.0.zip", "alpha", "1.1.0");
            plate.QueueItem = (target, r) => new UpdateQueueItem(target, r.Local.Info!.ModId, r.Local.Info.Name, r.Local.Info.Version ?? "",
                r.LatestCompatible!.ModVersion ?? "", null, zip);

            main.SelectedTab = AppTab.Server;         // раздел открыли: опрос — сразу
            Sta.WaitFor(() => plate.ShowUpdates);
            main.SelectedTab = AppTab.Mods;            // дальше опрос раздела тесту не нужен
            Sta.WaitFor(() => !main.Mods.IsBusy);
            using var dialogs = new ScriptedDialogs(Answer.Yes);
            var progress = new List<string>();
            plate.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(HostedServerViewModel.UpdatesProgressText) && plate.UpdatesProgressText.Length > 0)
                    progress.Add(plate.UpdatesProgressText);
            };

            var run = plate.UpdateAllCommand.ExecuteAsync(null);
            Assert.True(plate.IsUpdating);             // ставится — ход на плашке
            Assert.False(plate.UpdateAllCommand.CanExecute(null));
            Sta.Wait(run);
            Sta.WaitFor(() => !plate.IsUpdating && !main.Mods.IsQueueRunning);
            Sta.WaitFor(() => { lock (sent) return sent.Count > 0; });

            // вопрос — тот же, что у «Обновить всё» в «Моих модах»; «останови сервер» — нет: по логу он остановлен
            Assert.Equal([Loc.T("mods.updateAllConfirm", 1, "• alpha: 1.0.0 → 1.1.0")], dialogs.Asked);
            Assert.True(_host.Server.Has("/Mods/alpha_1.1.0.zip"));
            Assert.Contains(Loc.T("queue.titleRunning", 0, 1), progress);
            Assert.False(plate.ShowUpdates);           // поставили — плашка ушла
            Assert.Equal(ModsViewModel.WithStartHint(Loc.T("queue.finished", 1, 0), true), plate.UpdatesNote);
            var message = Assert.Single(sent);
            Assert.Equal(Loc.T("notify.ev.modsUpdated", 1), message.Title);
            Assert.Equal(["• alpha: 1.0.0 → 1.1.0"], message.Details.Select(d => d.Text));
            // в истории модов — как у «Обновить всё» вкладки
            Assert.Equal(ModHistorySource.UpdateAll, ModHistory.ReadEntries(ModHistory.FileFor(_host.Profile.Id)).Last(e => e.ModId == "alpha").Source);
        }
        finally
        {
            main.HostedWatch.Stop();
        }
    });

    [Fact]
    public void UpdateAll_Declined_ChangesNothing() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        _due = [Update("alpha", "1.0.0", "1.1.0")];
        var started = 0;
        var vm = new HostedServerViewModel(null!)
        {
            Updates = Updates(),
            RunUpdates = (_, _) =>
            {
                started++;
                return Task.FromResult(false); // «Нет» на вопрос
            },
        };
        vm.OnProfileSwitched(_host.Profile);
        Poll(vm);

        Sta.Wait(vm.UpdateAllCommand.ExecuteAsync(null));

        Assert.Equal(1, started);
        Assert.False(vm.IsUpdating);
        Assert.True(vm.ShowUpdates);                  // обновления на месте — плашка тоже
        Assert.Equal("", vm.UpdatesNote);
    });

    /// <summary>Часы для тестов: время двигает сам тест.</summary>
    private sealed class TestTime(DateTime utc) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utc;

        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
    }
}

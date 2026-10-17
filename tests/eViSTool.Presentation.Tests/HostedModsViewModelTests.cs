using System.IO.Compression;
using eViSTool.Core;
using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Packs;
using eViSTool.Core.Profiles;
using eViSTool.Core.Tests.Hosting;
using eViSTool.Presentation.ViewModels;
using eViSTool.Presentation.ViewModels.Mods;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Вкладка «Мои моды» для сервера у хостера: моды читаются по FTP (поддельный сервер «как G-Portal» над временной
/// папкой), вопрос «останови сервер в панели хостера» вместо «сервер запущен», удаление с копией на этой машине,
/// выключение через serverconfig.json, опрос изменений со стороны, модпак. Главная модель настоящая (профиль
/// добавляется в её список): ей принадлежат активный профиль и выбранная вкладка. Данные пользователя не трогаются.
/// </summary>
public sealed class HostedModsViewModelTests : IDisposable
{
    private readonly HostedFixture _host = new();
    private readonly IAppWindows? _oldWindows = AppWindows.IsSet ? AppWindows.Current : null;

    public HostedModsViewModelTests() => _host.GPortalLayout();

    public void Dispose()
    {
        AppWindows.Current = _oldWindows!;
        _host.Dispose();
    }

    /// <summary>Главная модель с серверным профилем у хостера в качестве активного; ждёт, пока моды прочитаны.</summary>
    private (MainViewModel Main, ModsViewModel Mods, ProfileViewModel Profile) Open(bool wait = true)
    {
        var language = Loc.Instance.Language; // главная модель выставляет язык из настроек — другим тестам его не менять
        var main = new MainViewModel { AutoCheckUpdates = false }; // автопроверка ходила бы в модбазу
        Loc.Instance.SetLanguage(language);
        main.Profiles.Clear(); // профили пользователя из настроек тесту не нужны (и «Установить также в» не должно их предлагать)
        var profile = new ProfileViewModel(_host.Profile, _ => { });
        main.Profiles.Add(profile);
        main.ActiveProfile = profile;
        if (wait) WaitRead(main.Mods);
        return (main, main.Mods, profile);
    }

    /// <summary>Сервер, по файлам которого версию игры не узнать: ни version.txt, ни лога, ни LastLaunchMods в конфиге.</summary>
    private void NoVersionOnTheServer()
    {
        File.Delete(Path.Combine(_host.Dir, "server", "version.txt"));
        File.Delete(Path.Combine(_host.Dir, "server", "Logs", "server-main.log"));
        _host.Server.Put("/serverconfig.json", "{ \"ModPaths\": [\"Mods\"] }");
    }

    private static void WaitRead(ModsViewModel mods) =>
        Sta.WaitFor(() => mods.HostedSource.Resolved is not null && !mods.HostedSource.IsLoading);

    private static ModRowViewModel Row(ModsViewModel mods, string modId) => mods.Rows.Single(r => r.ModId == modId);

    private JArray? Disabled() => JObject.Parse(_host.Server.Text("/serverconfig.json"))["WorldConfig"]?["DisabledMods"] as JArray;

    // ---- чтение

    [Fact]
    public void Open_ReadsModsFromTheServer_AndShowsTheHostersModWithoutActions() => Sta.Run(() =>
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        _host.PutMod("/Mods/beta_2.0.0.zip", "beta", "2.0.0");

        var (main, mods, profile) = Open();

        Assert.True(mods.IsHosted);
        Assert.False(mods.IsLocalProfile);
        Assert.False(mods.GameMissing);          // «укажи папку игры» — только для своего профиля
        Assert.False(mods.HostedVersionMissing);
        Assert.Equal("1.22.7", mods.GameVersion?.ToString());
        Assert.Equal("1.22.7", profile.HostedGameVersion);
        Assert.Equal(Loc.T("hosted.versionSourceFile"), profile.HostedGameVersionSource);
        Assert.Equal(Loc.T("mods.readFromServer"), mods.StatusText);
        Assert.Equal(3, mods.Rows.Count);
        Assert.Equal("2.0.0", Row(mods, "beta").Installed);
        var hoster = mods.Rows.Single(r => r.FileName == "GPortalPlayerCount.cs");
        Assert.Equal("", hoster.ModId);
        Assert.False(hoster.CanUpdate);
        // у мода хостера свой статус: не «не читается» и не проблема; переключатель и действия скрыты
        Assert.Equal(Loc.T("status.hosterMod"), hoster.StatusText);
        Assert.False(hoster.IsProblem);
        Assert.False(hoster.CanChange);
        Assert.False(hoster.CanFindInCatalog);
        Assert.Equal(ModSide.Server, hoster.Side);
        mods.Selected = hoster;
        Assert.False(mods.CanChangeSelected);
        Assert.Equal(0, mods.ProblemCount);
        mods.Selected = Row(mods, "beta");
        Assert.True(mods.CanChangeSelected);
        Assert.Equal(0, mods.ConfigCount("alpha")); // ссылки «Настройки мода» у хостера нет
        Assert.Equal(AppTab.Mods, main.SelectedTab);
    });

    [Fact]
    public void Open_WithoutAnyVersionOnTheServer_ShowsTheVersionBannerAndOpensSettings() => Sta.Run(() =>
    {
        NoVersionOnTheServer();

        var (main, mods, _) = Open();

        Assert.True(mods.HostedVersionMissing);
        Assert.Equal(Loc.T("hosted.versionMissing"), mods.HostedVersionMissingText);
        Assert.False(mods.GameMissing);
        main.SelectedTab = AppTab.Mods;
        mods.OpenHostedSettingsCommand.Execute(null);
        Assert.Equal(AppTab.Settings, main.SelectedTab);
    });

    [Fact]
    public void Open_ServerNotReachable_SaysSoInTheStatus_WithoutAPopup() => Sta.Run(() =>
    {
        _host.Server.FailOn = (op, _) => op == "connect";
        _host.Server.FailKind = RemoteFailure.Auth;

        var (_, mods, _) = Open(wait: false);
        Sta.WaitFor(() => mods.StatusText.StartsWith(Loc.T("mods.readFailed", "").TrimEnd()));

        Assert.Contains(Loc.T("hosted.errAuth"), mods.StatusText);
        Assert.Empty(mods.Rows);
    });

    [Fact]
    public void ProfileSwitchWhileAnotherTabIsOpen_DoesNotGoToTheServerUntilTheTabOpens() => Sta.Run(() =>
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var language = Loc.Instance.Language;
        var main = new MainViewModel { AutoCheckUpdates = false, SelectedTab = AppTab.Settings };
        Loc.Instance.SetLanguage(language);
        main.Profiles.Clear();
        var profile = new ProfileViewModel(_host.Profile, _ => { });
        main.Profiles.Add(profile);

        main.ActiveProfile = profile;
        Assert.False(_host.Server.Calls.ContainsKey("connect"));
        Assert.Equal(Loc.T("hosted.reading"), main.Mods.StatusText);

        main.SelectedTab = AppTab.Mods;
        WaitRead(main.Mods);
        Assert.Single(main.Mods.Rows, r => r.ModId == "alpha");
    });

    // ---- вопрос «останови сервер»

    [Fact]
    public void Delete_AsksToStopTheServer_KeepsACopy_AndRemindsToStartIt() => Sta.Run(() =>
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        _host.PutMod("/Mods/beta_2.0.0.zip", "beta", "2.0.0");
        var (_, mods, _) = Open();
        using var dialogs = new ScriptedDialogs(Answer.Yes, Answer.Yes);

        Sta.Wait(mods.DeleteCommand.ExecuteAsync(Row(mods, "alpha")));

        Assert.Equal(2, dialogs.Asked.Count);
        Assert.EndsWith(Loc.T("hosted.deleteNote"), dialogs.Asked[0]); // корзины на сервере нет — об этом в вопросе
        Assert.Equal(Loc.T("hosted.stopAsk", _host.Profile.Name), dialogs.Asked[1]);
        Assert.False(_host.Server.Has("/Mods/alpha_1.0.0.zip"));
        Assert.Single(ModBackupStore.ForProfile(_host.Profile).List("alpha")); // копия осталась на этой машине
        Assert.DoesNotContain(mods.Rows, r => r.ModId == "alpha");
        Assert.EndsWith(Loc.T("hosted.startHint"), mods.StatusText);
        var history = ModHistory.ReadEntries(ModHistory.FileFor(_host.Profile.Id));
        Assert.Equal(("alpha", "1.0.0", (string?)null), (history.Single().ModId, history.Single().From, history.Single().To));

        // «Да» помнится: второй мод удаляется с одним вопросом — про само удаление
        using var again = new ScriptedDialogs(Answer.Yes);
        Sta.Wait(mods.DeleteCommand.ExecuteAsync(Row(mods, "beta")));
        Assert.Single(again.Asked);
        Assert.False(_host.Server.Has("/Mods/beta_2.0.0.zip"));
    });

    [Fact]
    public void Delete_SaysTheServerHasNoRecycleBin_InTheQuestionAndTheResult() => Sta.Run(() =>
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var (_, mods, _) = Open();
        using var dialogs = new ScriptedDialogs(Answer.Yes, Answer.Yes);

        Sta.Wait(mods.DeleteCommand.ExecuteAsync(Row(mods, "alpha")));

        Assert.Equal(Loc.T("hosted.deleteConfirm", "alpha", "1.0.0") + "\n\n" + Loc.T("hosted.deleteNote"), dialogs.Asked[0]);
        Assert.DoesNotContain(Loc.T("mods.deleteConfirm", "alpha", "1.0.0"), dialogs.Asked[0]); // про Корзину — неправда
        Assert.StartsWith(Loc.T("hosted.deletedOk", "alpha"), mods.StatusText);
        Assert.DoesNotContain(Loc.T("mods.deletedOk", "alpha"), mods.StatusText);
    });

    [Fact]
    public void Delete_StopQuestionAnsweredNo_ChangesNothingOnTheServer() => Sta.Run(() =>
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var (_, mods, _) = Open();
        using var dialogs = new ScriptedDialogs(Answer.Yes, Answer.No);

        Sta.Wait(mods.DeleteCommand.ExecuteAsync(Row(mods, "alpha")));

        Assert.Equal(2, dialogs.Asked.Count);
        Assert.True(_host.Server.Has("/Mods/alpha_1.0.0.zip"));
        Assert.Empty(_host.Server.Writes);
        Assert.Empty(ModBackupStore.ForProfile(_host.Profile).List("alpha"));
    });

    [Fact]
    public void Delete_WithTheReminderTurnedOffInTheProfile_DoesNotAskToStop() => Sta.Run(() =>
    {
        _host.Settings.SkipStopReminder = true;
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var (_, mods, _) = Open();
        using var dialogs = new ScriptedDialogs(Answer.Yes);

        Sta.Wait(mods.DeleteCommand.ExecuteAsync(Row(mods, "alpha")));

        Assert.Single(dialogs.Asked);
        Assert.False(_host.Server.Has("/Mods/alpha_1.0.0.zip"));
    });

    [Fact]
    public void HostersModAndFolderMods_CanNotBeChanged_AndNothingIsWrittenToTheServer() => Sta.Run(() =>
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        _host.Server.Put("/Mods/foldermod/modinfo.json", """{ "modid": "folder", "name": "Folder", "version": "1.0.0" }""");
        var (_, mods, _) = Open();
        using var dialogs = new ScriptedDialogs(); // любой вопрос уронил бы тест

        var hoster = mods.Rows.Single(r => r.FileName == "GPortalPlayerCount.cs");
        Sta.Wait(mods.DeleteCommand.ExecuteAsync(hoster));
        Assert.Equal(Loc.T("hosted.hosterMod"), mods.StatusText);

        Sta.Wait(mods.DeleteCommand.ExecuteAsync(Row(mods, "folder")));
        Assert.Equal(Loc.T("hosted.folderModReadOnly"), mods.StatusText);
        Sta.Wait(mods.ToggleEnabledCommand.ExecuteAsync(Row(mods, "folder")));
        Assert.Equal(Loc.T("hosted.folderModReadOnly"), mods.StatusText);

        Assert.Empty(_host.Server.Writes);
    });

    // ---- включение и выключение

    [Fact]
    public void Toggle_DisablesTheModInServerConfig_AfterTheStopQuestion() => Sta.Run(() =>
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var (_, mods, _) = Open();
        using var dialogs = new ScriptedDialogs(Answer.Yes);

        Sta.Wait(mods.ToggleEnabledCommand.ExecuteAsync(Row(mods, "alpha")));

        Assert.Equal([Loc.T("hosted.stopAsk", _host.Profile.Name)], dialogs.Asked);
        Assert.Contains("alpha", Disabled()!.Select(t => (string?)t));
        Assert.False(Row(mods, "alpha").IsEnabled);
        Assert.EndsWith(Loc.T("hosted.startHint"), mods.StatusText);
    });

    [Fact]
    public void Toggle_StopQuestionAnsweredNo_LeavesTheConfigAndPutsTheSwitchBack() => Sta.Run(() =>
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var (_, mods, _) = Open();
        using var dialogs = new ScriptedDialogs(Answer.No);
        var before = _host.Server.Text("/serverconfig.json");

        Sta.Wait(mods.ToggleEnabledCommand.ExecuteAsync(Row(mods, "alpha")));

        Assert.Equal(before, _host.Server.Text("/serverconfig.json"));
        Assert.True(Row(mods, "alpha").IsEnabled);
    });

    [Fact]
    public async Task ModDisabler_DisablesAModOfAHostedServer_ByItsRemotePath()
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var target = _host.Target;

        var mod = ModDisabler.ModAt(target, "/Mods/alpha_1.0.0.zip");
        Assert.Null(mod.Info);                  // путь на сервере, на этой машине такого файла нет — не читаем
        Assert.Equal("/Mods/alpha_1.0.0.zip", mod.Path);

        await ModDisabler.DisableModAsync(target, "/Mods/alpha_1.0.0.zip");
        Assert.Contains("alpha", Disabled()!.Select(t => (string?)t));
    }

    // ---- изменения со стороны

    [Fact]
    public void Poll_NoticesAModAddedOnTheServer_ButStaysQuietWhileAnOperationRuns() => Sta.Run(() =>
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var (_, mods, _) = Open();
        var lists = _host.Server.Calls["list"];

        Sta.Wait(mods.PollHostedAsync()); // ничего не менялось — перечитывать нечего
        Assert.Equal(Loc.T("mods.readFromServer"), mods.StatusText);
        Assert.Single(mods.Rows, r => r.ModId == "alpha");

        _host.PutMod("/Mods/gamma_3.0.0.zip", "gamma", "3.0.0");
        mods.IsBusy = true;                     // идёт своя операция
        Sta.Wait(mods.PollHostedAsync());
        Assert.DoesNotContain(mods.Rows, r => r.ModId == "gamma");

        mods.IsBusy = false;
        Sta.Wait(mods.PollHostedAsync());
        Sta.WaitFor(() => mods.Rows.Any(r => r.ModId == "gamma"));
        Assert.Equal(Loc.T("mods.changedOutside"), mods.StatusText);
        Assert.True(_host.Server.Calls["list"] > lists);
    });

    [Fact]
    public void Poll_TimerRunsOnlyWhileTheTabIsOpen() => Sta.Run(() =>
    {
        var (main, mods, _) = Open();
        var timer = typeof(HostedModSource).GetField("_poll", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var poll = (IUiTimer)timer.GetValue(mods.HostedSource)!;

        Assert.True(poll.IsEnabled);
        Assert.Equal(HostedModSource.PollInterval, poll.Interval);
        Assert.Equal(TimeSpan.FromSeconds(30), HostedModSource.PollInterval);
        main.SelectedTab = AppTab.Settings;
        Assert.False(poll.IsEnabled);
        main.SelectedTab = AppTab.Mods;
        Assert.True(poll.IsEnabled);
    });

    [Fact]
    public void Poll_RereadFailed_KeepsTheReadErrorInTheStatus() => Sta.Run(() =>
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var (_, mods, _) = Open();

        _host.PutMod("/Mods/gamma_3.0.0.zip", "gamma", "3.0.0"); // отпечаток сменился
        _host.Server.FailOn = (op, _) => op is "read" or "download"; // а прочитать моды уже не выходит
        Sta.Wait(mods.PollHostedAsync());

        Assert.StartsWith(Loc.T("mods.readFailed", "").TrimEnd(), mods.StatusText);
        Assert.NotEqual(Loc.T("mods.changedOutside"), mods.StatusText);
    });

    [Fact]
    public void Poll_FirstReadFailed_TriesAgainAndShowsTheMods() => Sta.Run(() =>
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        _host.Server.FailOn = (op, _) => op == "connect";
        var (_, mods, _) = Open(wait: false);
        Sta.WaitFor(() => mods.StatusText.StartsWith(Loc.T("mods.readFailed", "").TrimEnd()) && !mods.HostedSource.IsLoading);

        _host.Server.FailOn = null;                 // связь вернулась
        Sta.Wait(mods.PollHostedAsync());
        WaitRead(mods);

        Assert.Single(mods.Rows, r => r.ModId == "alpha");
        Assert.Equal(Loc.T("mods.readFromServer"), mods.StatusText);
    });

    [Fact]
    public void SwitchFromOwnProfile_TheDiskWatchDoesNotWipeTheHostedList() => Sta.Run(() =>
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var data = Directory.CreateDirectory(Path.Combine(_host.Dir, "local-data")).FullName;
        Directory.CreateDirectory(Path.Combine(data, "Mods"));
        File.WriteAllText(Path.Combine(data, ProfileResolver.ServerConfigName), """{ "ModPaths": [ "Mods" ] }""");
        var language = Loc.Instance.Language;
        var main = new MainViewModel { AutoCheckUpdates = false };
        Loc.Instance.SetLanguage(language);
        main.Profiles.Clear();
        var local = new ProfileViewModel(new GameProfile { Id = "own-" + Guid.NewGuid().ToString("N")[..6], Name = "Own", Kind = ProfileKind.Server, DataDir = data }, _ => { });
        var hosted = new ProfileViewModel(_host.Profile, _ => { });
        main.Profiles.Add(local);
        main.Profiles.Add(hosted);
        main.ActiveProfile = local;
        main.Mods.SetActive(true);                  // таймер диска запущен, отметка своего профиля запомнена
        Sta.WaitFor(() => main.Mods.Profile is not null);

        main.ActiveProfile = hosted;
        WaitRead(main.Mods);
        var until = DateTime.UtcNow.AddSeconds(3);  // таймер диска тикает раз в 2 с
        Sta.WaitFor(() => DateTime.UtcNow > until);

        Assert.Contains(main.Mods.Rows, r => r.ModId == "alpha");
        Assert.NotEqual(Loc.T("mods.changedOutside"), main.Mods.StatusText);
    });

    [Fact]
    public void Check_DoesNotReadTheServerAgainWhileTheScanIsYoungerThanTenSeconds() => Sta.Run(() =>
    {
        // без версии игры проверка кончается сразу после чтения — до модбазы, в сеть тест не выходит
        NoVersionOnTheServer();
        var (_, mods, _) = Open();
        var now = DateTime.UtcNow;
        mods.HostedSource.Now = () => now;
        // скан состоялся «до» подмены часов: считаем его свежим вручную — перечитаем и сверим
        Sta.Wait(mods.HostedSource.ReloadAsync());
        var lists = _host.Server.Calls["list"];

        Sta.Wait(mods.CheckCommand.ExecuteAsync(null));
        Assert.Equal(lists, _host.Server.Calls["list"]);
        Assert.Equal(Loc.T("mods.noGameVersion"), mods.StatusText);

        now += TimeSpan.FromSeconds(11);
        Sta.Wait(mods.CheckCommand.ExecuteAsync(null));
        Assert.True(_host.Server.Calls["list"] > lists);
    });

    // ---- откат из истории

    [Fact]
    public void RollbackSession_ThreeUpdatesFromLocalCopiesAndOneRemoval_AsksToStopTheServerOnce() => Sta.Run(() =>
    {
        foreach (var id in new[] { "alpha", "beta", "gamma" })
        {
            _host.PutMod($"/Mods/{id}_1.1.0.zip", id, "1.1.0");
            // прежняя версия — в копиях на этой машине: модбаза откату не нужна
            var dir = Directory.CreateDirectory(ModBackupStore.ForProfile(_host.Profile).DirFor(id)).FullName;
            File.Copy(_host.Zip($"{id}_1.0.0.zip", id, "1.0.0"), Path.Combine(dir, $"{id}_1.0.0.zip"));
        }
        _host.PutMod("/Mods/delta_1.0.0.zip", "delta", "1.0.0");
        var file = ModHistory.FileFor(_host.Profile.Id);
        using (ModHistory.Begin(ModHistorySource.UpdateAll))
        {
            foreach (var id in new[] { "alpha", "beta", "gamma" }) ModHistory.Record(file, id, id, "1.0.0", "1.1.0");
            ModHistory.Record(file, "delta", "delta", null, "1.0.0");
        }
        var (_, mods, _) = Open();
        var now = mods.Rows.Where(r => r.ModId.Length > 0).ToDictionary(r => r.ModId, r => (string?)r.Installed);
        var session = new HistorySessionRow(ModHistory.Read(file).Single(), now, server: true);
        var lists = _host.Server.Calls["list"];
        using var dialogs = new ScriptedDialogs(Answer.Yes, Answer.Yes);

        Sta.Wait(mods.RollbackSessionCommand.ExecuteAsync(session));
        Sta.WaitFor(() => !mods.IsQueueRunning && _host.Server.Has("/Mods/gamma_1.0.0.zip"));
        Sta.WaitFor(() => !mods.IsQueueRunning && !mods.IsBusy);

        Assert.Equal(2, dialogs.Asked.Count);                       // сам откат и один вопрос про остановку на всё
        Assert.Equal(Loc.T("hosted.stopAsk", _host.Profile.Name), dialogs.Asked[1]);
        foreach (var id in new[] { "alpha", "beta", "gamma" })
        {
            Assert.True(_host.Server.Has($"/Mods/{id}_1.0.0.zip"), id);
            Assert.False(_host.Server.Has($"/Mods/{id}_1.1.0.zip"), id);
        }
        Assert.False(_host.Server.Has("/Mods/delta_1.0.0.zip"));   // поставленный в сеансе убран
        Assert.Single(ModBackupStore.ForProfile(_host.Profile).List("delta")); // с копией на этой машине
        Assert.True(_host.Server.Calls["list"] > lists);
        Sta.WaitFor(() => mods.Rows.Any(r => r.ModId == "alpha" && r.Installed == "1.0.0"));
        Assert.DoesNotContain(mods.Rows, r => r.ModId == "delta");
    });

    // ---- установка и модпак

    [Fact]
    public void InstallZip_ToAHostedServer_UploadsAndAsksToStopTheServerOnce() => Sta.Run(() =>
    {
        var statuses = new List<string>();
        var service = new ModInstallService((MainViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel)),
            new ModUpdater(new eViSTool.Core.ModDb.ModDbClient()), statuses.Add, () => null);
        var target = _host.Target;
        using var dialogs = new ScriptedDialogs(Answer.Yes);
        var (installed, problems) = (new List<string>(), new List<string>());

        Assert.True(service.ConfirmIfRunning(target));
        Assert.True(service.ConfirmIfRunning(target)); // помнится
        Assert.False(service.IsRunning(target));       // по FTP запущен ли сервер, не видно
        var done = service.InstallZipAsync(target, _host.Zip("gamma_1.0.0.zip", "gamma", "1.0.0"), interactive: false, installed, problems);
        Sta.Wait(done);

        Assert.Single(dialogs.Asked);
        Assert.True(done.Result, string.Join("; ", problems));
        Assert.Equal(["gamma 1.0.0"], installed);
        Assert.Contains(Loc.T("mods.uploading", "gamma", "1.0.0"), statuses);
        Assert.True(_host.Server.Has("/Mods/gamma_1.0.0.zip"));
    });

    [Fact]
    public void StopReminder_RemembersYesForTenMinutes_NotNo_AndIsSwitchedOffByTheProfile()
    {
        var clock = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        var reminder = new HostedStopReminder(() => clock);
        var target = _host.Target;

        using (var no = new ScriptedDialogs(Answer.No, Answer.Yes))
        {
            Assert.False(reminder.Confirm(target));
            Assert.True(reminder.Confirm(target));   // «Нет» не запоминается — спросили снова
            Assert.Equal(2, no.Asked.Count);
        }

        clock += HostedStopReminder.Memory - TimeSpan.FromSeconds(1);
        using (var quiet = new ScriptedDialogs())
            Assert.True(reminder.Confirm(target));   // девять минут пятьдесят девять секунд — ещё помним

        clock += TimeSpan.FromSeconds(2);
        using (var again = new ScriptedDialogs(Answer.No))
        {
            Assert.False(reminder.Confirm(target));  // десять минут прошло — спрашиваем
            Assert.Single(again.Asked);
        }

        _host.Settings.SkipStopReminder = true;
        using (var off = new ScriptedDialogs())
            Assert.True(reminder.Confirm(_host.Target)); // флажок профиля: не спрашивать совсем
    }

    [Fact]
    public void ImportPack_GoesToTheHostedServer_AfterTheStopQuestion_AndSaysModConfigWasSkipped() => Sta.Run(() =>
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var (_, mods, _) = Open();
        var path = Pack();
        AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of(("ConfirmPackImport", _ => new PackImportChoice(Mirror: false, ApplyConfig: true)));
        using var dialogs = new ScriptedDialogs(Answer.Yes);

        Sta.Wait(mods.ImportPackFileAsync(path));

        Assert.Equal([Loc.T("hosted.stopAsk", _host.Profile.Name)], dialogs.Asked);
        Assert.True(_host.Server.Has("/Mods/alpha_1.1.0.zip"));
        Assert.False(_host.Server.Has("/Mods/alpha_1.0.0.zip"));
        Assert.True(_host.Server.Has("/Mods/fresh_1.0.0.zip"));
        Assert.Contains("fresh", Disabled()!.Select(t => (string?)t)); // в паке он выключен
        Assert.Contains(dialogs.Warned, w => w.Contains(Loc.T("pack.hostedConfigSkipped"))); // настроек модов по FTP нет — в отчёте
        Assert.Single(ModBackupStore.ForProfile(_host.Profile).List("alpha"));
    });

    [Fact]
    public void ExportPack_IsNotOfferedForAHostedServer() => Sta.Run(() =>
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var (_, mods, _) = Open();
        using var dialogs = new ScriptedDialogs(); // окно экспорта открылось бы через AppWindows и уронило тест

        Sta.Wait(mods.ExportPackCommand.ExecuteAsync(null));

        Assert.Empty(dialogs.Asked);
    });

    private string Pack()
    {
        var alpha = _host.Zip("alpha_1.1.0.zip", "alpha", "1.1.0");
        var fresh = _host.Zip("fresh_1.0.0.zip", "fresh", "1.0.0");
        var path = Path.Combine(_host.Dir, "test" + PackManifest.Extension);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(alpha, PackManifest.ModsFolder + "alpha_1.1.0.zip");
            zip.CreateEntryFromFile(fresh, PackManifest.ModsFolder + "fresh_1.0.0.zip");
            using (var w = new StreamWriter(zip.CreateEntry(PackManifest.ConfigFolder + "AlphaConfig.json").Open())) w.Write("{ \"Speed\": 2 }");
            var manifest = new PackManifest
            {
                Name = "hosted", IncludesModConfig = true, Kind = ProfileKind.Server,
                Mods =
                [
                    new() { ModId = "alpha", Name = "alpha", Version = "1.1.0", FileName = "alpha_1.1.0.zip", Bundled = true, Sha256 = PackBuilder.Sha256Of(alpha) },
                    new() { ModId = "fresh", Name = "fresh", Version = "1.0.0", FileName = "fresh_1.0.0.zip", Bundled = true, Enabled = false, Sha256 = PackBuilder.Sha256Of(fresh) },
                ],
            };
            using var w2 = new StreamWriter(zip.CreateEntry(PackManifest.FileName).Open());
            w2.Write(JsonConvert.SerializeObject(manifest));
        }
        return path;
    }
}

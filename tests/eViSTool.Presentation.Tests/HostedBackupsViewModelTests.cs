using System.ComponentModel;
using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Tests;
using eViSTool.Core.Tests.Hosting;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Вкладка «Копии» раздела «Сервер» у хостера: поддельный FTP «как G-Portal», настоящие миры SQLite и настоящие логи
/// G-Portal (откуда качать, решает лог). Кнопка: остановлен — сам мир, работает — копия игры, работает без копий —
/// понятный отказ; отмена; список и удаление в Корзину; восстановление — вопрос, отказ на работающем сервере и когда по
/// логу не понять, успех, после вылета — вопрос всегда, лог не прочитать — причина в строке; настройки сохраняются в
/// профиль без правки профиля; операция переживает смену профиля и отменяется удалением профиля; смена языка.
/// </summary>
public sealed class HostedBackupsViewModelTests : IDisposable
{
    private const string Main = "/Logs/server-main.log";
    private const string World = "/Saves/MyVintageStoryWorld.vcdbs";
    private const string GameCopy = "/Backups/genbackup.vcdbs";

    private readonly HostedFixture _host = new();
    private readonly string _copies;
    private readonly IAppWindows? _oldWindows = AppWindows.IsSet ? AppWindows.Current : null;
    private int _saved;   // настройки только сохранили (remembered)
    private int _edited;  // «правка профиля» — перечитала бы моды

    public HostedBackupsViewModelTests()
    {
        _host.GPortalLayout(log: false);
        _copies = Path.Combine(_host.Dir, "copies");
        _host.Settings.BackupDir = _copies;
    }

    public void Dispose()
    {
        AppWindows.Current = _oldWindows!;
        _host.Dispose();
    }

    // ---------- стенд

    private string ServerLocal(string remote) => Path.Combine([_host.Server.Root, .. remote.Split('/', StringSplitOptions.RemoveEmptyEntries)]);

    /// <summary>Настоящий мир SQLite на «сервере» с этими строками; время файла — сейчас.</summary>
    private void PutWorld(string remote, params string[] rows)
    {
        var local = ServerLocal(remote);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        foreach (var f in new[] { local, local + "-wal", local + "-shm" }) File.Delete(f);
        SqliteWorld.WriteClean(local, rows);
    }

    /// <summary>Лог со временем файла, как у G-Portal: последняя строка только что записана, часы сервера — UTC+2.</summary>
    private void PutLog(string[] lines)
    {
        var marks = new HostedRunMarks();
        foreach (var line in lines) marks.Add(line);
        var local = _host.Server.Put(Main, HostedLogSamples.Bytes(lines));
        File.SetLastWriteTimeUtc(local, DateTime.SpecifyKind(marks.LastTime!.Value.AddHours(-2), DateTimeKind.Utc));
    }

    private string[] CopyFiles() =>
        Directory.Exists(_copies) ? [.. Directory.GetFiles(_copies).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)] : [];

    private HostedBackupsViewModel Open(HostedWorldJobs? jobs = null)
    {
        var profile = new ProfileViewModel(_host.Profile, _ => _edited++, _ => _saved++);
        var vm = new HostedBackupsViewModel(null, jobs ?? new HostedWorldJobs());
        vm.OnProfileSwitched(profile);
        vm.SetActive(true);
        Sta.Wait(vm.RefreshAsync());
        return vm;
    }

    /// <summary>Нажать «Скачать копию сейчас» и дождаться конца; что вкладка показывала по ходу — в works.</summary>
    private static List<string> BackupNow(HostedBackupsViewModel vm)
    {
        var works = new List<string>();
        PropertyChangedEventHandler track = (_, e) =>
        {
            if (e.PropertyName == nameof(HostedBackupsViewModel.WorkText) && vm.WorkText.Length > 0) works.Add(vm.WorkText);
        };
        vm.PropertyChanged += track;
        Assert.True(vm.BackupNowCommand.CanExecute(null));
        vm.BackupNowCommand.Execute(null);
        Assert.True(vm.IsWorking);
        Sta.WaitFor(() => !vm.IsWorking);
        Sta.Wait(vm.Refreshed); // список — после итога, в пуле потоков
        vm.PropertyChanged -= track;
        return works;
    }

    /// <summary>Своя копия этого профиля прямо в папку копий — тем же ядром, в заданное время.</summary>
    private HostedWorldBackup MakeCopy(DateTime utc)
    {
        var backups = new HostedWorldBackups(_host.Target) { Time = new FixedTime(utc) };
        return Task.Run(() => backups.CreateAsync(serverStopped: true, null, default)).GetAwaiter().GetResult().Backup;
    }

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    // ---------- кнопка

    [Fact]
    public void BackupNow_StoppedServer_DownloadsTheWorldItself_WithProgress() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        PutWorld(World, "one", "two");
        PutWorld(GameCopy, "game copy"); // копия игры есть, но сервер остановлен — качаем сам мир
        var vm = Open();
        Assert.Equal(Loc.T("sched.lastNone"), vm.LastText);

        var works = BackupNow(vm);

        var row = Assert.Single(vm.Backups);
        Assert.Equal(HostedWorldOrigin.World, row.Backup.Origin);
        Assert.Equal(Loc.T("hostedbk.originWorld"), row.OriginText);
        Assert.Equal(["one", "two"], SqliteWorld.Read(row.Backup.Path));
        Assert.Equal(Loc.T("hostedbk.done", row.Name, Sizes.Format(row.Backup.Size + row.Backup.ExtraSize)), vm.StatusText);
        Assert.Equal("", vm.ErrorText);
        Assert.True(row.HasExtra); // рядом — настройки и данные модов
        // ход: сначала проверка лога, потом «Скачиваю мир: X из X» до конца
        Assert.Equal(Loc.T("hostedbk.checking"), works[0]);
        var size = new FileInfo(ServerLocal(World)).Length;
        Assert.Contains(Loc.T("hostedbk.stepWorld", Sizes.Format(size), Sizes.Format(size)), works);
        Assert.DoesNotContain(_host.Server.DownloadOffsets, d => d.Path == GameCopy);
        Assert.StartsWith(Loc.T("sched.last", "").TrimEnd(), vm.LastText);
        Assert.True(vm.BackupNowCommand.CanExecute(null)); // кончилось — снова можно
    });

    [Fact]
    public void BackupNow_RunningServer_TakesTheFreshGameCopy() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Running);
        PutWorld(World, "live");
        PutWorld(GameCopy, "from genbackup");
        File.SetLastWriteTimeUtc(ServerLocal(GameCopy), DateTime.UtcNow.AddMinutes(-10));
        var vm = Open();

        var works = BackupNow(vm);

        var row = Assert.Single(vm.Backups);
        Assert.Equal(HostedWorldOrigin.GameBackup, row.Backup.Origin);
        Assert.Equal(Loc.T("hostedbk.originGame"), row.OriginText);
        Assert.Equal(Loc.T("hostedbk.sourceTip", GameCopy), row.OriginTip);
        Assert.Equal(["from genbackup"], SqliteWorld.Read(row.Backup.Path));
        Assert.Contains(works, w => w.StartsWith(Loc.T("hostedbk.stepGameCopy", "", "")[..8], StringComparison.Ordinal));
        Assert.DoesNotContain(_host.Server.DownloadOffsets, d => d.Path == World); // работающий мир не качали
    });

    [Fact]
    public void BackupNow_RunningServerWithoutGameCopies_SaysWhatToDo_AndSavesNothing() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Running);
        PutWorld(World, "live");
        var vm = Open();

        BackupNow(vm);

        Assert.Equal(Loc.T("hosted.worldNeedsStop"), vm.ErrorText); // останови сервер или сделай /genbackup
        Assert.Equal("", vm.StatusText);
        Assert.Empty(vm.Backups);
        Assert.DoesNotContain(CopyFiles(), f => f.EndsWith(".vcdbs", StringComparison.Ordinal) || f.Contains(".part", StringComparison.Ordinal));
        Assert.DoesNotContain(_host.Server.DownloadOffsets, d => d.Path.EndsWith(".vcdbs", StringComparison.Ordinal)); // мир не качали
    });

    [Fact]
    public void BackupNow_UnreadableLog_IsTreatedAsRunning_AndTheMessageSaysWhy() => Sta.Run(() =>
    {
        PutWorld(World, "live"); // лога нет: остановлен ли сервер, не понять — мир не трогаем
        var vm = Open();

        BackupNow(vm);

        Assert.Equal(Loc.T("hostedbk.stateUnknown") + " " + Loc.T("hosted.worldNeedsStop"), vm.ErrorText);
        Assert.DoesNotContain(_host.Server.DownloadOffsets, d => d.Path.EndsWith(".vcdbs", StringComparison.Ordinal));
    });

    [Fact]
    public void Cancel_StopsTheDownload_AndLeavesNoHalfCopy() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        PutWorld(World, [.. Enumerable.Range(0, 40).Select(i => i + new string('x', 8000))]);
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _host.Server.OnBefore = (op, path) =>
        {
            if (op != "download" || path != World) return;
            reached.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        };
        var vm = Open();

        vm.BackupNowCommand.Execute(null);
        Sta.WaitFor(() => reached.IsSet);
        Assert.True(vm.CancelCommand.CanExecute(null));
        Assert.False(vm.BackupNowCommand.CanExecute(null)); // вторая копия того же профиля не начнётся
        vm.CancelCommand.Execute(null);
        Assert.Equal(Loc.T("hostedbk.cancelling"), vm.WorkText);
        Assert.False(vm.CancelCommand.CanExecute(null));
        release.Set();
        Sta.WaitFor(() => !vm.IsWorking);

        Assert.Equal(Loc.T("hostedbk.cancelled"), vm.StatusText);
        Assert.Equal("", vm.ErrorText);
        Assert.Empty(vm.Backups);
        Assert.DoesNotContain(CopyFiles(), f => f.Contains(".vcdbs", StringComparison.Ordinal));
    });

    [Fact]
    public void TheDownloadGoesOn_WhenTheProfileIsSwitched_AndItsProgressIsBackOnReturn() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        PutWorld(World, "one");
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _host.Server.OnBefore = (op, path) =>
        {
            if (op != "download" || path != World) return;
            reached.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        };
        var jobs = new HostedWorldJobs();
        var vm = Open(jobs);
        var hosted = new ProfileViewModel(_host.Profile, _ => { });
        using var other = new HostedFixture();
        var second = new ProfileViewModel(other.Profile, _ => { });

        vm.BackupNowCommand.Execute(null);
        Sta.WaitFor(() => reached.IsSet);
        vm.OnProfileSwitched(second);
        Assert.False(vm.IsWorking); // у другого профиля ничего не идёт
        Assert.True(vm.BackupNowCommand.CanExecute(null));
        Assert.NotNull(jobs.Running(_host.Profile.Id));

        vm.OnProfileSwitched(hosted);
        Assert.True(vm.IsWorking); // вернулись — снова виден ход
        Assert.NotEqual("", vm.WorkText);
        release.Set();
        Sta.WaitFor(() => !vm.IsWorking);
        Sta.WaitFor(() => vm.Backups.Count == 1);
        Assert.StartsWith(Loc.T("hostedbk.done", "", "")[..5], vm.StatusText);

        // итог, случившийся, пока смотрели другой профиль, виден при возвращении
        vm.OnProfileSwitched(second);
        Assert.Equal("", vm.StatusText);
        vm.OnProfileSwitched(hosted);
        Assert.StartsWith(Loc.T("hostedbk.done", "", "")[..5], vm.StatusText);
    });

    // ---------- список и удаление

    [Fact]
    public void List_NewestFirst_AndDelete_GoesToTheTrash_WithItsArchiveAndInfo() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        PutWorld(World, "a");
        var older = MakeCopy(new DateTime(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc));
        var newer = MakeCopy(new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc));
        var vm = Open();

        Assert.Equal([newer.Name, older.Name], vm.Backups.Select(r => r.Name));
        Assert.Equal(Dates.Stamp(newer.Time), vm.Backups[0].TimeText);
        Assert.Equal(Loc.T("sched.total", 2, Sizes.Format(vm.Backups.Sum(r => r.Backup.Size + r.Backup.ExtraSize))), vm.TotalText);
        Assert.Equal(Loc.T("sched.last", Dates.Stamp(newer.Time)), vm.LastText);

        using (var no = new ScriptedDialogs(Answer.No))
        {
            Sta.Wait(vm.DeleteCommand.ExecuteAsync(vm.Backups[1]));
            Assert.Equal([Loc.T("sched.deleteAsk", older.Name)], no.Asked);
        }
        Assert.Equal(2, vm.Backups.Count);

        using (new ScriptedDialogs(Answer.Yes)) Sta.Wait(vm.DeleteCommand.ExecuteAsync(vm.Backups[1]));

        Assert.Equal([newer.Name], vm.Backups.Select(r => r.Name));
        Assert.False(File.Exists(older.Path));
        Assert.False(File.Exists(HostedWorldBackups.ExtraFor(older.Path)));
        Assert.False(File.Exists(HostedWorldBackups.InfoFor(older.Path)));
        Assert.True(File.Exists(newer.Path));
    });

    /// <summary>Корзина, в окне ошибки которой нажали «Отмена» (так бросает VB FileSystem.DeleteFile); на вопрос — «Да».</summary>
    private sealed class CancelledTrash : TestUiPlatform, IUiPlatform
    {
        public override Answer Ask(string text, AskButtons buttons, Answer? byDefault = null, bool warning = false) => Answer.Yes;
        void IUiPlatform.MoveToTrash(string path) => throw new OperationCanceledException("cancelled in the error dialog");
    }

    [Fact]
    public void Delete_CancelledInTheSystemErrorDialog_SaysSo_AndKeepsTheCopy() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        PutWorld(World, "a");
        var copy = MakeCopy(new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc));
        var vm = Open();

        Ui.Platform = new CancelledTrash();
        try
        {
            Sta.Wait(vm.DeleteCommand.ExecuteAsync(vm.Backups[0])); // не роняет окно отчётом о сбое
        }
        finally
        {
            Ui.Platform = new ScriptedPlatform(null);
        }

        Assert.Equal(Loc.T("sched.failed", "cancelled in the error dialog"), vm.ErrorText);
        Assert.True(File.Exists(copy.Path));
        Assert.Single(vm.Backups);
    });

    [Fact]
    public void NetworkFolders_OnWindows_HaveNoRecycleBin_SoTheQuestionSaysPermanently()
    {
        Assert.False(HostedBackupsViewModel.HasNoRecycleBin(Path.Combine(Path.GetTempPath(), "copy.vcdbs")));
        Assert.Equal(OperatingSystem.IsWindows(), HostedBackupsViewModel.HasNoRecycleBin(@"\\nas\share\copies\copy.vcdbs"));
        Assert.Equal(OperatingSystem.IsWindows(), HostedBackupsViewModel.HasNoRecycleBin(@"\\?\UNC\nas\share\copies\copy.vcdbs"));
        Assert.NotEqual(Loc.T("sched.deleteAsk", "x"), Loc.T("hostedbk.deleteAskNetwork", "x"));
    }

    [Fact]
    public void RemovingTheProfile_CancelsItsDownload() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        PutWorld(World, "one");
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _host.Server.OnBefore = (op, path) =>
        {
            if (op != "download" || path != World) return;
            reached.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        };
        var language = Loc.Instance.Language; // главная модель выставляет язык из настроек — другим тестам его не менять
        var main = new MainViewModel { AutoCheckUpdates = false };
        Loc.Instance.SetLanguage(language);
        try
        {
            main.Profiles.Clear();
            var own = new ProfileViewModel(new GameProfile { Id = "own-" + Guid.NewGuid().ToString("N"), Name = "Свой", Kind = ProfileKind.Server }, _ => { });
            var hosted = new ProfileViewModel(_host.Profile, _ => { });
            main.Profiles.Add(own);
            main.Profiles.Add(hosted);
            main.ActiveProfile = own;
            var job = main.HostedWorldJobs.StartBackup(_host.Target, scheduled: false);
            Assert.NotNull(job);
            Sta.WaitFor(() => reached.IsSet);

            using (new ScriptedDialogs(Answer.Yes)) Sta.Wait(main.RemoveProfileCommand.ExecuteAsync(hosted));

            Assert.True(job.IsCancelling); // копию удалённого профиля не доделываем
            release.Set();
            Sta.WaitFor(() => job.IsFinished);
            Assert.False(job.Completion.Result.Ok);
            Assert.Null(main.HostedWorldJobs.Running(_host.Profile.Id));
            Assert.DoesNotContain(CopyFiles(), f => f.Contains(".vcdbs", StringComparison.Ordinal));
        }
        finally
        {
            release.Set();
            main.HostedWatch.Stop();
        }
    });

    // ---------- восстановление

    /// <summary>Окно «Восстановить мир» отвечает так; что ему показали — в asked.</summary>
    private static void RestoreDialog(HostedRestoreChoice? answer, List<object?[]> asked) =>
        AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of((nameof(IAppWindows.ConfirmHostedRestore), args =>
        {
            asked.Add(args);
            return answer;
        }));

    [Fact]
    public void Restore_DeclinedInTheQuestion_TouchesNothing() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        PutWorld(World, "old");
        MakeCopy(new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc));
        var vm = Open();
        var asked = new List<object?[]>();
        RestoreDialog(null, asked);

        Sta.Wait(vm.RestoreCommand.ExecuteAsync(vm.Backups[0]));

        var args = Assert.Single(asked);
        Assert.Equal(vm.Backups[0].Backup, args[0]);
        Assert.Equal(_host.Profile.Name, args[1]);
        Assert.False(vm.IsWorking);
        Assert.Empty(_host.Server.Writes);
    });

    [Fact]
    public void Restore_OnARunningServer_RefusesAtOnce_WithoutAskingWhetherItIsStopped() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        PutWorld(World, "old");
        MakeCopy(new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc));
        PutLog(HostedLogSamples.Running); // сервер запустили снова
        var vm = Open();
        RestoreDialog(new HostedRestoreChoice(true), []);

        // по логу работает — вопрос «остановлен?» был бы пустым: «Да» всё равно упёрлось бы в лог
        using (var dialogs = new ScriptedDialogs()) // любой вопрос уронил бы тест
            Sta.Wait(vm.RestoreCommand.ExecuteAsync(vm.Backups[0]));

        Assert.Equal(Loc.T("hostedbk.restoreRunning", Loc.T("hostedbk.stateRunning")), vm.ErrorText);
        Assert.False(vm.IsWorking);
        Assert.False(vm.IsChecking);
        Assert.Empty(_host.Server.Writes); // на сервер ничего не писали
        Assert.Equal(["old"], SqliteWorld.Read(ServerLocal(World)));
    });

    [Fact]
    public void Restore_StoppedServer_PutsTheWorldBack_WithModSettings_AndKeepsTheOldOneAsBak() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        PutWorld(World, "old");
        _host.Server.Put("/ModConfig/carryon.json", """{ "a": 1 }""");
        MakeCopy(new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc));
        PutWorld(World, "new");
        _host.Server.Put("/ModConfig/carryon.json", """{ "a": 2 }""");
        var vm = Open();
        RestoreDialog(new HostedRestoreChoice(true), []);
        var copy = vm.Backups[0].Backup;
        var works = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HostedBackupsViewModel.WorkText) && vm.WorkText.Length > 0) works.Add(vm.WorkText);
        };

        // по логу остановлен — вопроса «останови сервер» нет (ScriptedDialogs без ответов уронил бы тест)
        using (new ScriptedDialogs()) Sta.Wait(vm.RestoreCommand.ExecuteAsync(vm.Backups[0]));
        Sta.WaitFor(() => !vm.IsWorking);

        Assert.Equal("", vm.ErrorText);
        Assert.StartsWith(Loc.T("hostedbk.restored", Dates.Stamp(copy.Time)), vm.StatusText);
        Assert.Contains(Loc.T("hostedbk.restoredKept", World + ".evistool.bak"), vm.StatusText);
        Assert.EndsWith(Loc.T("hostedbk.restoredStart"), vm.StatusText);
        Assert.Equal(["old"], SqliteWorld.Read(ServerLocal(World)));
        Assert.Equal(["new"], SqliteWorld.Read(ServerLocal(World + ".evistool.bak"))); // нынешний мир — рядом как bak
        Assert.Contains("\"a\": 1", _host.Server.Text("/ModConfig/carryon.json"));
        Assert.Contains(works, w => w.StartsWith(Loc.T("hostedbk.stepUpload", "", "")[..8], StringComparison.Ordinal));
    });

    [Fact]
    public void Restore_WithoutTheBox_LeavesModSettingsAlone() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        PutWorld(World, "old");
        _host.Server.Put("/ModConfig/carryon.json", """{ "a": 1 }""");
        MakeCopy(new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc));
        PutWorld(World, "new");
        _host.Server.Put("/ModConfig/carryon.json", """{ "a": 2 }""");
        var vm = Open();
        RestoreDialog(new HostedRestoreChoice(false), []);
        var copy = vm.Backups[0].Backup;

        using (new ScriptedDialogs()) Sta.Wait(vm.RestoreCommand.ExecuteAsync(vm.Backups[0]));
        Sta.WaitFor(() => !vm.IsWorking);

        Assert.Equal(["old"], SqliteWorld.Read(ServerLocal(World)));
        Assert.Contains("\"a\": 2", _host.Server.Text("/ModConfig/carryon.json"));
        // без строки «файлов настроек возвращено»
        Assert.Equal(Loc.T("hostedbk.restored", Dates.Stamp(copy.Time)) + " " + Loc.T("hostedbk.restoredKept", World + ".evistool.bak") + " "
                     + Loc.T("hostedbk.restoredStart"), vm.StatusText);
    });

    [Fact]
    public void Restore_AfterACrash_AlwaysAsks_EvenWithTheReminderOff_AndHoldsTheScheduleMeanwhile() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        PutWorld(World, "old");
        MakeCopy(new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc));
        PutWorld(World, "broken");
        _host.Settings.SkipStopReminder = true; // вопрос перед модами выключен — перед восстановлением он всё равно есть
        var jobs = new HostedWorldJobs(state: (_, _) => Task.FromResult(HostedServerState.Crashed)); // упал: хостер может поднять его снова
        var vm = Open(jobs);
        var held = new List<bool>();
        AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of((nameof(IAppWindows.ConfirmHostedRestore), _ =>
        {
            held.Add(jobs.IsHeld(_host.Profile.Id)); // пока спрашиваем, расписание копию не начнёт
            return new HostedRestoreChoice(false);
        }));

        using (var no = new ScriptedDialogs(Answer.No))
        {
            Sta.Wait(vm.RestoreCommand.ExecuteAsync(vm.Backups[0]));
            Assert.Equal([Loc.T("hostedbk.stopAsk", _host.Profile.Name)], no.Asked);
        }
        Assert.Empty(_host.Server.Writes);
        Assert.Equal("", vm.ErrorText);
        Assert.False(jobs.IsHeld(_host.Profile.Id));

        // и «Да» минуту назад не помнится: снова спрашиваем
        using (var yes = new ScriptedDialogs(Answer.Yes))
        {
            Sta.Wait(vm.RestoreCommand.ExecuteAsync(vm.Backups[0]));
            Assert.Equal([Loc.T("hostedbk.stopAsk", _host.Profile.Name)], yes.Asked);
        }
        Sta.WaitFor(() => !vm.IsWorking);

        Assert.Equal("", vm.ErrorText);
        Assert.Equal(["old"], SqliteWorld.Read(ServerLocal(World)));
        Assert.Equal([true, true], held);
        Assert.False(jobs.IsHeld(_host.Profile.Id));
    });

    [Fact]
    public void Restore_WhenTheLogSaysNothing_AsksAndThenRefuses_WithoutTouchingTheWorld() => Sta.Run(() =>
    {
        PutWorld(World, "old"); // лога нет: остановлен ли сервер, не понять
        MakeCopy(new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc));
        var vm = Open();
        RestoreDialog(new HostedRestoreChoice(true), []);

        using (var yes = new ScriptedDialogs(Answer.Yes))
        {
            Sta.Wait(vm.RestoreCommand.ExecuteAsync(vm.Backups[0]));
            Assert.Equal([Loc.T("hostedbk.stopAsk", _host.Profile.Name)], yes.Asked);
        }

        Assert.Equal(Loc.T("hostedbk.restoreUnknown"), vm.ErrorText);
        Assert.False(vm.IsWorking);
        Assert.False(vm.IsChecking);
        Assert.Empty(_host.Server.Writes);
    });

    [Fact]
    public void Restore_WhenTheLogCannotBeReadAtAll_SaysWhy_InsteadOfCrashing() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        PutWorld(World, "old");
        MakeCopy(new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc));
        _host.Settings.Root = "/../elsewhere"; // корень вне корня FTP: путь к логам не собрать (InvalidDataException)
        var vm = Open();
        RestoreDialog(new HostedRestoreChoice(true), []);

        using (new ScriptedDialogs()) Sta.Wait(vm.RestoreCommand.ExecuteAsync(vm.Backups[0])); // и без вопроса

        Assert.StartsWith(Loc.T("hostedbk.restoreFailed", ""), vm.ErrorText);
        Assert.Contains("..", vm.ErrorText);
        Assert.False(vm.IsChecking);
        Assert.Empty(_host.Server.Writes);
    });

    // ---------- папка и язык

    [Fact]
    public void BackupDir_SetBackWhileItsCheckRuns_DoesNotLeaveTheCheckingLine() => Sta.Run(() =>
    {
        var vm = Open();

        vm.BackupDirText = Path.Combine(_host.Dir, "elsewhere");
        Assert.True(vm.BackupDirChecking);
        vm.BackupDirText = _copies; // вернули сохранённую, пока проверка шла

        Assert.False(vm.BackupDirChecking);
        Sta.Wait(Task.Delay(300)); // первая проверка возвращается и ничего не показывает
        Assert.False(vm.BackupDirChecking);
        Assert.Equal("", vm.BackupDirOk);
        Assert.Equal("", vm.BackupDirError);
        Assert.Equal(_copies, _host.Settings.BackupDir);
    });

    [Fact]
    public void LanguageSwitch_RebuildsTheRefusal_AndTheFolderLines() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        PutWorld(World, "old");
        MakeCopy(new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc));
        var language = Loc.Instance.Language;
        var other = language == "ru" ? "en" : "ru";
        var vm = Open(new HostedWorldJobs(state: (_, _) => Task.FromResult(HostedServerState.Unknown)));
        RestoreDialog(new HostedRestoreChoice(false), []);
        try
        {
            using (new ScriptedDialogs(Answer.Yes)) Sta.Wait(vm.RestoreCommand.ExecuteAsync(vm.Backups[0]));
            var refusal = vm.ErrorText;
            Assert.Equal(Loc.T("hostedbk.restoreUnknown"), refusal);
            vm.BackupDirText = Path.Combine(_host.Dir, "elsewhere");
            Sta.WaitFor(() => vm.BackupDirOk.Length > 0);
            var saved = vm.BackupDirOk;

            Loc.Instance.SetLanguage(other);
            vm.OnLanguageChanged();

            Assert.Equal(Loc.T("hostedbk.restoreUnknown"), vm.ErrorText);
            Assert.NotEqual(refusal, vm.ErrorText);
            Assert.Equal(Loc.T("hostedbk.dirSaved"), vm.BackupDirOk);
            Assert.NotEqual(saved, vm.BackupDirOk);

            // что не так с папкой, пишет ядро — после смены языка проверяем заново
            vm.BackupDirText = "relative/path";
            Sta.WaitFor(() => vm.BackupDirError.Length > 0);
            var error = vm.BackupDirError;
            Loc.Instance.SetLanguage(language);
            vm.OnLanguageChanged();
            Sta.WaitFor(() => !vm.BackupDirChecking && vm.BackupDirError.Length > 0);
            Assert.Equal(Loc.T(OperatingSystem.IsWindows() ? "backupdir.notFull" : "backupdir.notFullLinux"), vm.BackupDirError);
            Assert.NotEqual(error, vm.BackupDirError);
            Assert.Equal(Path.Combine(_host.Dir, "elsewhere"), _host.Settings.BackupDir); // не подошла — осталась прежняя
        }
        finally
        {
            Loc.Instance.SetLanguage(language);
        }
    });

    // ---------- вкладка в разделе «Сервер»

    [Fact]
    public void ServerSection_HostedProfile_HasTheBackupsTab_OthersFallBackToTheConsole() => Sta.Run(() =>
    {
        var main = (MainViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        var active = typeof(MainViewModel).GetField("_activeProfile", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        active.SetValue(main, new ProfileViewModel(_host.Profile, _ => { }));
        var server = new ServerViewModel(main);
        server.OnProfileSwitched();

        server.Tab = ServerTab.Backups;
        Assert.True(server.IsBackupsTab);
        Assert.False(server.IsHostedOverviewTab);
        Assert.False(server.IsNotifyTab);
        Assert.True(server.HostedBackups.HasProfile);
        server.OnProfileSwitched(); // правка того же профиля — вкладка остаётся
        Assert.Equal(ServerTab.Backups, server.Tab);

        // у игрового профиля (и у своего сервера) этой вкладки нет — назад к консоли
        active.SetValue(main, new ProfileViewModel(new GameProfile { Id = "hb-client", Name = "Игра", Kind = ProfileKind.Client }, _ => { }));
        server.OnProfileSwitched();
        Assert.Equal(ServerTab.Console, server.Tab);
        Assert.False(server.HostedBackups.HasProfile);
        Assert.True(server.IsHostedOverviewTab);
    });

    // ---------- настройки

    [Fact]
    public void Settings_AreSavedToTheProfileAtOnce_WithoutEditingIt() => Sta.Run(() =>
    {
        var vm = Open();
        Assert.Equal(HostedBackupSchedule.Off, vm.Schedule);
        Assert.Equal("7", vm.KeepText);
        Assert.Equal(Loc.T("hostedbk.nextOff"), vm.NextText);

        vm.Schedule = HostedBackupSchedule.Daily;
        Assert.Equal(HostedBackupSchedule.Daily, _host.Settings.BackupSchedule);
        Assert.Equal(1, _saved);
        Assert.Equal(Loc.T("hostedbk.nextSoon"), vm.NextText); // копий нет — скоро

        vm.KeepText = "3";
        Assert.Equal(3, _host.Settings.BackupKeep);
        vm.KeepText = "много";
        Assert.Equal(Loc.T("sched.keepError"), vm.KeepError);
        Assert.Equal(3, _host.Settings.BackupKeep); // плохое число не сохраняется
        Assert.Equal(2, _saved);

        var dir = Path.Combine(_host.Dir, "elsewhere");
        vm.BackupDirText = dir;
        Sta.WaitFor(() => !vm.BackupDirChecking && vm.BackupDirOk.Length > 0);
        Assert.Equal(dir, _host.Settings.BackupDir);
        Assert.Equal(Loc.T("hostedbk.dirSaved"), vm.BackupDirOk);
        Assert.Equal(3, _saved);

        vm.BackupDirText = "relative/path";
        Sta.WaitFor(() => vm.BackupDirError.Length > 0);
        Assert.Equal(dir, _host.Settings.BackupDir); // не полный путь — остаётся прежняя
        Assert.Equal(3, _saved);

        vm.DefaultBackupDirCommand.Execute(null);
        Sta.WaitFor(() => vm.BackupDirOk.Length > 0);
        Assert.Null(_host.Settings.BackupDir);
        Assert.Equal(Loc.T("hostedbk.dirSavedDefault"), vm.BackupDirOk);
        Assert.Equal(HostedWorldBackups.DefaultDir(_host.Profile.Id), vm.BackupDirDefault);
        Assert.Equal(0, _edited); // моды не перечитывались
    });

    [Fact]
    public void Settings_OfAnotherProfile_AreShownWhenSwitchedTo() => Sta.Run(() =>
    {
        _host.Settings.BackupSchedule = HostedBackupSchedule.Weekly;
        _host.Settings.BackupKeep = 12;
        var vm = Open();
        Assert.Equal(HostedBackupSchedule.Weekly, vm.Schedule);
        Assert.Equal("12", vm.KeepText);
        Assert.Equal(_copies, vm.BackupDirText);

        using var other = new HostedFixture();
        vm.OnProfileSwitched(new ProfileViewModel(other.Profile, _ => { }, _ => _saved++));
        Assert.Equal(HostedBackupSchedule.Off, vm.Schedule);
        Assert.Equal("7", vm.KeepText);
        Assert.Equal("", vm.BackupDirText);
        Assert.Equal(0, _saved); // показать — не сохранить
        Assert.Equal(HostedBackupSchedule.Weekly, _host.Settings.BackupSchedule);
        Assert.Equal(12, _host.Settings.BackupKeep);
    });
}

using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Вкладка «Расписание»: поле папки для копий применяется, когда поле отпустили или нажали Enter. Отпускание —
/// привязка поля (LostFocus), Enter окно передаёт в <see cref="ServerScheduleViewModel.CommitBackupDir"/>. Ответ проверки,
/// которую обогнали (вернули сохранённую папку, сменили профиль), не показывается; строки под полем — на текущем языке.
/// </summary>
public sealed class ServerScheduleBackupDirTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "evistool-sched-" + Guid.NewGuid().ToString("N"));
    private readonly GameProfile _profile;

    public ServerScheduleBackupDirTests()
    {
        var data = Directory.CreateDirectory(Path.Combine(_tmp, "data")).FullName;
        _profile = new GameProfile { Id = "sched-" + Guid.NewGuid().ToString("N")[..8], Name = "sched", Kind = ProfileKind.Server, DataDir = data };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
        try { File.Delete(ServerAutomation.FileFor(_profile.Id)); } catch (IOException) { }
    }

    private static void Until(Func<bool> done, string what = "условия")
    {
        try { Sta.WaitFor(done); }
        catch (TimeoutException) { throw new TimeoutException("Не дождались: " + what); }
    }

    /// <summary>
    /// Вкладка на профиле из папки, настройки которой уже прочитаны (иначе правки не сохраняются). Признак чтения —
    /// число копий из файла настроек, не равное значению по умолчанию: подписи вкладки здесь не годятся, главной модели
    /// в тесте нет, и чтение обрывается на ней уже после того, как настройки легли в поля.
    /// </summary>
    private void WithSchedule(Action<ServerScheduleViewModel> body) => Sta.Run(() =>
    {
        new ServerAutomation { BackupKeep = 5 }.Save(_profile.Id);
        var schedule = new ServerViewModel(null!).Schedule;
        schedule.OnProfileSwitched(_profile, () => null);
        Until(() => schedule.KeepText == "5", "чтение настроек");
        body(schedule);
    });

    // Enter в поле: набранный путь проверяется, папка создаётся, настройка уходит в файл расписания
    [Fact]
    public void CommitBackupDir_AppliesTypedFolder() => WithSchedule(schedule =>
    {
        var dir = Path.Combine(_tmp, "copies");

        schedule.CommitBackupDir(dir);

        Assert.Equal(dir, schedule.BackupDirText);
        Until(() => schedule.BackupDirOk.Length > 0 || schedule.BackupDirError.Length > 0);
        Assert.Equal("", schedule.BackupDirError);
        Assert.False(schedule.BackupDirChecking);
        Assert.True(Directory.Exists(dir));
        Until(() => ServerAutomation.Load(_profile.Id).BackupDir == dir);
    });

    // не полный путь машина с сервером не примет: ошибка под полем, настройка остаётся прежней
    [Fact]
    public void CommitBackupDir_RelativePath_ShowsErrorAndKeepsSetting() => WithSchedule(schedule =>
    {
        schedule.CommitBackupDir("copies");

        Until(() => schedule.BackupDirOk.Length > 0 || schedule.BackupDirError.Length > 0);
        Assert.NotEqual("", schedule.BackupDirError);
        Assert.Equal("", schedule.BackupDirOk);
        Assert.Null(ServerAutomation.Load(_profile.Id).BackupDir);
    });

    // пустое поле (и null из поля, у которого нет текста) — копии снова в Backups сервера
    [Fact]
    public void CommitBackupDir_Empty_ReturnsToDefaultFolder() => WithSchedule(schedule =>
    {
        var dir = Path.Combine(_tmp, "copies");
        schedule.CommitBackupDir(dir);
        Until(() => schedule.BackupDirOk.Length > 0);
        Until(() => ServerAutomation.Load(_profile.Id).BackupDir == dir);

        schedule.CommitBackupDir(null);

        Assert.Equal("", schedule.BackupDirText);
        Until(() => schedule.BackupDirOk.Length > 0, "сообщение «копии в Backups сервера»"); // придёт после сохранения
        Assert.Null(ServerAutomation.Load(_profile.Id).BackupDir);
    });

    // тот же текст ещё раз (Enter, потом уход из поля): повторно ничего не проверяется и не пишется
    [Fact]
    public void CommitBackupDir_SameTextTwice_ChecksOnce() => WithSchedule(schedule =>
    {
        var dir = Path.Combine(_tmp, "copies");
        schedule.CommitBackupDir(dir);
        Until(() => schedule.BackupDirOk.Length > 0);
        var ok = schedule.BackupDirOk;
        schedule.BackupDirOk = "";

        schedule.CommitBackupDir(dir);

        Assert.False(schedule.BackupDirChecking);
        Assert.Equal("", schedule.BackupDirOk); // повторной проверки не было — сообщение не вернулось
        Assert.NotEqual("", ok);
    });

    // поле вернули к сохранённой папке, пока шла проверка другой: «Проверяю папку…» не остаётся висеть
    [Fact]
    public void BackupDir_SetBackWhileItsCheckRuns_DoesNotLeaveTheCheckingLine() => WithSchedule(schedule =>
    {
        var dir = Path.Combine(_tmp, "copies");
        schedule.CommitBackupDir(dir);
        Assert.True(schedule.BackupDirChecking);

        schedule.CommitBackupDir(""); // сохранена папка по умолчанию — к ней и вернули

        Assert.False(schedule.BackupDirChecking);
        Until(() => Directory.Exists(dir), "первой проверки");
        Sta.Wait(Task.Delay(300)); // первая проверка возвращается и ничего не показывает
        Assert.False(schedule.BackupDirChecking);
        Assert.Equal("", schedule.BackupDirOk);
        Assert.Equal("", schedule.BackupDirError);
        Assert.Null(ServerAutomation.Load(_profile.Id).BackupDir);
    });

    // профиль сменили, пока шла проверка папки: её ответ новому не показывается и ни в чьи настройки не пишется
    [Fact]
    public void BackupDir_CheckOfThePreviousProfile_IsDroppedOnSwitch() => WithSchedule(schedule =>
    {
        var other = new GameProfile
        {
            Id = "sched-" + Guid.NewGuid().ToString("N")[..8], Name = "other", Kind = ProfileKind.Server,
            DataDir = Directory.CreateDirectory(Path.Combine(_tmp, "other")).FullName,
        };
        new ServerAutomation { BackupKeep = 9 }.Save(other.Id);
        try
        {
            var dir = Path.Combine(_tmp, "copies");
            schedule.CommitBackupDir(dir);
            Assert.True(schedule.BackupDirChecking);

            schedule.OnProfileSwitched(other, () => null);

            Assert.False(schedule.BackupDirChecking);
            Until(() => schedule.KeepText == "9", "чтение настроек второго профиля");
            Until(() => Directory.Exists(dir), "проверки папки первого профиля");
            Sta.Wait(Task.Delay(300));
            Assert.Equal("", schedule.BackupDirOk);
            Assert.False(schedule.BackupDirChecking);
            Assert.Null(ServerAutomation.Load(_profile.Id).BackupDir);
            Assert.Equal(5, ServerAutomation.Load(_profile.Id).BackupKeep); // настройки второго в файл первого не попали
            Assert.Null(ServerAutomation.Load(other.Id).BackupDir);
        }
        finally
        {
            try { File.Delete(ServerAutomation.FileFor(other.Id)); } catch (IOException) { }
        }
    });

    // смена языка: «папка подошла» — на новом языке, а ошибку пишет проверка — она проходит заново
    [Fact]
    public void LanguageSwitch_RebuildsTheFolderLines() => WithSchedule(schedule =>
    {
        var language = Loc.Instance.Language;
        try
        {
            var dir = Path.Combine(_tmp, "copies");
            schedule.CommitBackupDir(dir);
            Until(() => schedule.BackupDirOk.Length > 0);
            var ok = schedule.BackupDirOk;

            Loc.Instance.SetLanguage(language == "ru" ? "en" : "ru");
            schedule.OnLanguageChanged();

            Assert.Equal(Loc.T("backupdir.saved"), schedule.BackupDirOk);
            Assert.NotEqual(ok, schedule.BackupDirOk);

            schedule.CommitBackupDir("relative");
            Until(() => schedule.BackupDirError.Length > 0);
            var error = schedule.BackupDirError;
            Loc.Instance.SetLanguage(language);
            schedule.OnLanguageChanged();

            Until(() => !schedule.BackupDirChecking && schedule.BackupDirError.Length > 0, "повторной проверки");
            Assert.Equal(Loc.T(OperatingSystem.IsWindows() ? "backupdir.notFull" : "backupdir.notFullLinux"), schedule.BackupDirError);
            Assert.NotEqual(error, schedule.BackupDirError);
            Assert.Equal("", schedule.BackupDirOk);
            Assert.Equal(dir, ServerAutomation.Load(_profile.Id).BackupDir); // не подошла — осталась прежняя
        }
        finally
        {
            Loc.Instance.SetLanguage(language);
        }
    });

    // папка не подошла, а к смене языка её уже можно создать: повторная проверка её не сохраняет — настройки от смены
    // языка не пишутся
    [Fact]
    public void LanguageSwitch_DoesNotSaveAFolderThatPassesNow() => WithSchedule(schedule =>
    {
        var language = Loc.Instance.Language;
        try
        {
            var blocker = Path.Combine(_tmp, "blocker");
            File.WriteAllText(blocker, "file, not a folder");
            var dir = Path.Combine(blocker, "copies");
            schedule.CommitBackupDir(dir);
            Until(() => !schedule.BackupDirChecking && schedule.BackupDirError.Length > 0, "первой проверки");
            File.Delete(blocker);

            Loc.Instance.SetLanguage(language == "ru" ? "en" : "ru");
            schedule.OnLanguageChanged();

            Until(() => !schedule.BackupDirChecking && schedule.BackupDirError.Length > 0, "повторной проверки");
            Sta.Wait(Task.Delay(200));
            Assert.Equal("", schedule.BackupDirOk);
            Assert.Null(ServerAutomation.Load(_profile.Id).BackupDir);
        }
        finally
        {
            Loc.Instance.SetLanguage(language);
        }
    });
}

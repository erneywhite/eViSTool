using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Вкладка «Расписание»: поле папки для копий применяется, когда поле отпустили или нажали Enter. Отпускание —
/// привязка поля (LostFocus), Enter окно передаёт в <see cref="ServerScheduleViewModel.CommitBackupDir"/>.
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
}

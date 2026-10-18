using eViSTool.Core.Hosting;
using Newtonsoft.Json;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// Настройки копий мира у хостера в профиле (расписание переживает сохранение, незнакомое значение — «выключено») и
/// удаление копии своим способом (окно — в Корзину): все файлы копии, только существующие, только из своей папки.
/// </summary>
public sealed class HostedBackupSettingsTests : IDisposable
{
    private readonly HostedFixture _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public void Schedule_IsSavedByName_AndAnUnknownValueReadsAsOff()
    {
        var saved = JsonConvert.SerializeObject(new HostedConnection { Host = "h", BackupSchedule = HostedBackupSchedule.Weekly });
        Assert.Contains("\"BackupSchedule\":\"Weekly\"", saved);
        Assert.Equal(HostedBackupSchedule.Weekly, JsonConvert.DeserializeObject<HostedConnection>(saved)!.BackupSchedule);

        // настройки из новой версии с незнакомым расписанием — не падают и не качают
        var future = JsonConvert.DeserializeObject<HostedConnection>("""{ "Host": "h", "BackupSchedule": "Hourly", "BackupKeep": 3 }""")!;
        Assert.Equal(HostedBackupSchedule.Off, future.BackupSchedule);
        Assert.Equal(3, future.BackupKeep);
        // старые настройки без поля — выключено
        Assert.Equal(HostedBackupSchedule.Off, JsonConvert.DeserializeObject<HostedConnection>("""{ "Host": "h" }""")!.BackupSchedule);
        Assert.Equal(HostedBackupSchedule.Weekly, new HostedConnection { BackupSchedule = HostedBackupSchedule.Weekly }.Clone().BackupSchedule);
    }

    [Fact]
    public void Delete_WithOwnRemover_TakesTheWorldArchiveAndInfo_OnlyThoseThatExist()
    {
        var dir = Path.Combine(_host.Dir, "copies");
        _host.Settings.BackupDir = dir;
        Directory.CreateDirectory(dir);
        var world = Path.Combine(dir, "Hosted-2026-10-18_09-00-00.vcdbs");
        File.WriteAllBytes(world, [1]);
        File.WriteAllBytes(HostedWorldBackups.InfoFor(world), [2]); // архива нет — его и не трогаем
        var backups = new HostedWorldBackups(_host.Target);
        var copy = Assert.Single(backups.List());
        var removed = new List<string>();

        backups.Delete(copy, path =>
        {
            removed.Add(path);
            File.Delete(path);
        });

        Assert.Equal([world, HostedWorldBackups.InfoFor(world)], removed);
        Assert.Empty(backups.List());

        // копия не из своей папки — отказ, ничего не удалено
        var stranger = new HostedWorldBackup(Path.Combine(_host.Dir, "other.vcdbs"), "other.vcdbs", DateTime.Now, 1);
        Assert.Throws<ArgumentException>(() => backups.Delete(stranger, removed.Add));
        Assert.Equal(2, removed.Count);
    }
}

using System.Runtime.InteropServices;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Server;

/// <summary>Резервная копия в списке — без пути: у удалённого сервера путь указывает на чужую машину.</summary>
public sealed record BackupEntry(string Name, DateTime Time, long Size, bool IsOwn)
{
    /// <summary>Размер архива с данными модов рядом с копией; 0 — его нет.</summary>
    public long ModDataSize { get; init; }

    /// <summary>Путь к файлу — только для сервера на этой машине (для «Показать в папке»).</summary>
    [JsonIgnore]
    public string? LocalPath { get; init; }
}

/// <summary>Итог восстановления: куда отложен прежний мир (null — его не было).</summary>
public sealed record RestoreResult(string? SafetyName)
{
    /// <summary>У копии был архив данных модов — они тоже вернулись (иначе остались текущие).</summary>
    public bool ModDataRestored { get; init; }
}

/// <summary>
/// Данные сервера, которые нужны вкладке «Расписание»: настройки расписания и резервные копии. Две реализации —
/// файлы на этой машине и агент на другой по сети, вкладка работает с обеими одинаково.
/// </summary>
public interface IServerData
{
    bool IsRemote { get; }
    Task<ServerAutomation> LoadAutomationAsync(CancellationToken ct = default);
    Task SaveAutomationAsync(ServerAutomation settings, CancellationToken ct = default);
    Task<IReadOnlyList<BackupEntry>> ListBackupsAsync(CancellationToken ct = default);

    /// <summary>Копия файла мира — при остановленном сервере (у работающего копию делает сам сервер, /genbackup).</summary>
    Task<BackupEntry> CopyWorldAsync(CancellationToken ct = default);

    /// <summary>Вернуть мир из копии — только при остановленном сервере; прежний мир откладывается рядом.</summary>
    Task<RestoreResult> RestoreAsync(string name, CancellationToken ct = default);

    /// <summary>Удалить копию — в Корзину той машины, где она лежит.</summary>
    Task DeleteBackupAsync(string name, CancellationToken ct = default);

    /// <summary>Можно ли складывать копии в эту папку — проверяет машина с сервером; null — можно, иначе что не так.</summary>
    Task<string?> CheckBackupDirAsync(string? dir, CancellationToken ct = default);

    /// <summary>Статистика за срок — итоги считает машина с сервером.</summary>
    Task<StatsReport> LoadStatsAsync(StatsPeriod period, CancellationToken ct = default);
    Task SetStatsEnabledAsync(bool enabled, CancellationToken ct = default);
    Task ClearStatsAsync(CancellationToken ct = default);
}

/// <summary>
/// Резервные копии и настройки расписания серверного профиля на этой машине. Этим пользуются и окно (свой сервер),
/// и агент (отвечая удалённому окну), поэтому логика одна.
/// </summary>
public sealed class ServerFiles(string profileId, string dataDir, string? backupPrefix, string? agentsDir = null)
{
    public string DataDir => dataDir;
    /// <summary>Где лежат копии: выбранная папка или Backups сервера.</summary>
    public string BackupsDir => Store.Dir;

    private BackupStore Store => new(dataDir, backupPrefix, LoadAutomation().BackupDir);

    public StatsStore Stats => new(StatsStore.DirFor(profileId, agentsDir));

    public ServerAutomation LoadAutomation() => ServerAutomation.Load(profileId, agentsDir);
    public void SaveAutomation(ServerAutomation settings) => settings.Save(profileId, agentsDir);

    public IReadOnlyList<BackupEntry> ListBackups()
    {
        // оборванное восстановление (процесс убили посреди подмены) — вернуть мир как был; не вышло — скажет запуск сервера
        try { WorldRestore.Recover(dataDir); } catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { }
        return ListBackupsAsIs();
    }

    private IReadOnlyList<BackupEntry> ListBackupsAsIs() =>
        [.. Store.List().Select(b => new BackupEntry(b.Name, b.Time, b.Size, b.IsOwn) { LocalPath = b.Path, ModDataSize = b.ModDataSize })];

    public BackupEntry CopyWorld(DateTime now)
    {
        var save = SaveFileOf(dataDir) ?? throw new InvalidOperationException(Loc.T("sched.noConfig"));
        var store = Store;
        // мир занят на время копии: сервер (в том числе сторож в агенте) не стартует посреди неё
        using var world = WorldLock.Take(dataDir, WorldLock.Copy);
        var made = store.CopySave(save, now);
        var settings = LoadAutomation();
        if (settings.BackupEnabled && settings.BackupKeep > 0) store.Prune(settings.BackupKeep);
        return new BackupEntry(made.Name, made.Time, made.Size, made.IsOwn) { LocalPath = made.Path, ModDataSize = made.ModDataSize };
    }

    public RestoreResult Restore(string name, DateTime now)
    {
        var store = Store;
        // имя — только из списка копий: путь «..\..\что-то» сюда не пройдёт
        var backup = store.Find(name) ?? throw new FileNotFoundException(Loc.T("sched.noBackup", name));
        var save = SaveFileOf(dataDir) ?? throw new InvalidOperationException(Loc.T("sched.noConfig"));
        // мир занят на всё восстановление; работает или запускается сервер — отказ (замок держит он)
        using var world = WorldLock.Take(dataDir, WorldLock.Restore);
        return new RestoreResult(store.Restore(backup, save, now)?.Name) { ModDataRestored = backup.ModDataSize > 0 };
    }

    public void DeleteBackup(string name)
    {
        var backup = Store.Find(name) ?? throw new FileNotFoundException(Loc.T("sched.noBackup", name));
        RecycleBin.Send(backup.Path);
        if (backup.ModDataSize > 0) RecycleBin.Send(WorldModData.ArchiveFor(backup.Path)); // данные модов — вместе с копией
        // у страховочных копий прежних версий рядом лежал журнал SQLite — он без копии не нужен
        foreach (var tail in new[] { "-wal", "-shm" })
            if (File.Exists(backup.Path + tail)) RecycleBin.Send(backup.Path + tail);
    }

    // ---- serverconfig.json — для окна на другой машине

    public string ConfigPath => Path.Combine(dataDir, ProfileResolver.ServerConfigName);

    public RemoteConfigFile ReadConfig()
    {
        var path = ConfigPath;
        if (!File.Exists(path)) return new RemoteConfigFile(false, "", StampOf(path), null, false);
        var stamp = StampOf(path); // до чтения: успеют поменять между ними — увидим это как расхождение
        var text = File.ReadAllText(path);
        var save = SaveFileOf(dataDir);
        return new RemoteConfigFile(true, text, stamp, File.GetLastWriteTimeUtc(path), save is not null && File.Exists(save));
    }

    /// <summary>
    /// Записать конфиг, если он не поменялся с тех пор, как его читали (или если просят перезаписать). Текст проверяется
    /// как JSON; пишется атомарно, прежний файл остаётся рядом (*.evistool.bak). Сервер должен быть остановлен — это
    /// проверяет вызывающий (агент).
    /// </summary>
    public ConfigSaveResult WriteConfig(ConfigSaveRequest request)
    {
        var path = ConfigPath;
        var root = ModConfigEditor.Parse(request.Text);
        using var world = WorldLock.Take(dataDir, WorldLock.Config); // сервер не стартует на полузаписанном конфиге
        if (!request.Force && StampOf(path) != request.ExpectedStamp) return new ConfigSaveResult(true, ReadConfig());
        ModConfigEditor.Save(path, root);
        return new ConfigSaveResult(false, ReadConfig());
    }

    /// <summary>Отметка версии файла: время записи и длина; нет файла — «-».</summary>
    public static string StampOf(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "-";
    }

    /// <summary>Файл мира профиля — из его serverconfig.json (null — конфига или пути в нём нет).</summary>
    public static string? SaveFileOf(string dataDir)
    {
        var config = Path.Combine(dataDir, ProfileResolver.ServerConfigName);
        if (!File.Exists(config)) return null;
        if (ServerConfigDocument.Load(config).Get("WorldConfig.SaveFileLocation") is not JValue { Value: string path } || path.Trim().Length == 0)
            return null;
        return Path.IsPathRooted(path) ? path : Path.Combine(dataDir, path);
    }
}

/// <summary>Сервер на этой машине: те же файлы, в фоне.</summary>
/// <summary>
/// Сервер на этой машине: файлы напрямую. Восстановление — через агента профиля, если он запущен: агент владеет
/// сервером и отменит ожидающий перезапуск сторожа (иначе сервер, упавший перед восстановлением, поднялся бы сам).
/// </summary>
public sealed class LocalServerData(ServerFiles files, Func<AgentClient?>? agent = null) : IServerData
{
    public ServerFiles Files => files;
    public bool IsRemote => false;
    public Task<ServerAutomation> LoadAutomationAsync(CancellationToken ct = default) => Task.Run(files.LoadAutomation, ct);
    public Task SaveAutomationAsync(ServerAutomation settings, CancellationToken ct = default) => Task.Run(() => files.SaveAutomation(settings), ct);
    public Task<IReadOnlyList<BackupEntry>> ListBackupsAsync(CancellationToken ct = default) => Task.Run(files.ListBackups, ct);
    public Task<BackupEntry> CopyWorldAsync(CancellationToken ct = default) => Task.Run(() => files.CopyWorld(DateTime.Now), ct);
    public Task<RestoreResult> RestoreAsync(string name, CancellationToken ct = default) =>
        agent?.Invoke() is { } client ? client.RestoreAsync(name, ct) : Task.Run(() => files.Restore(name, DateTime.Now), ct);
    public Task DeleteBackupAsync(string name, CancellationToken ct = default) => Task.Run(() => files.DeleteBackup(name), ct);
    public Task<string?> CheckBackupDirAsync(string? dir, CancellationToken ct = default) => Task.Run(() => BackupStore.CheckDir(dir), ct);
    public Task<StatsReport> LoadStatsAsync(StatsPeriod period, CancellationToken ct = default) => Task.Run(() => files.Stats.Report(period, DateTime.Now), ct);
    public Task SetStatsEnabledAsync(bool enabled, CancellationToken ct = default) => Task.Run(() => files.Stats.SetEnabled(enabled), ct);
    public Task ClearStatsAsync(CancellationToken ct = default) => Task.Run(files.Stats.Clear, ct);
}

/// <summary>Сервер на другой машине: всё через его агента.</summary>
public sealed class RemoteServerData(Func<AgentClient?> client) : IServerData
{
    public bool IsRemote => true;
    private AgentClient Client => client() ?? throw new InvalidOperationException(Loc.T("server.stateOffline"));

    public Task<ServerAutomation> LoadAutomationAsync(CancellationToken ct = default) => Client.GetAutomationAsync(ct);
    public Task SaveAutomationAsync(ServerAutomation settings, CancellationToken ct = default) => Client.SaveAutomationAsync(settings, ct);
    public Task<IReadOnlyList<BackupEntry>> ListBackupsAsync(CancellationToken ct = default) => Client.BackupsAsync(ct);
    public Task<BackupEntry> CopyWorldAsync(CancellationToken ct = default) => Client.CopyWorldAsync(ct);
    public Task<RestoreResult> RestoreAsync(string name, CancellationToken ct = default) => Client.RestoreAsync(name, ct);
    public Task DeleteBackupAsync(string name, CancellationToken ct = default) => Client.DeleteBackupAsync(name, ct);
    public Task<string?> CheckBackupDirAsync(string? dir, CancellationToken ct = default) => Client.CheckBackupDirAsync(dir, ct);
    public Task<StatsReport> LoadStatsAsync(StatsPeriod period, CancellationToken ct = default) => Client.StatsAsync(period, ct);
    public Task SetStatsEnabledAsync(bool enabled, CancellationToken ct = default) => Client.SetStatsEnabledAsync(enabled, ct);
    public Task ClearStatsAsync(CancellationToken ct = default) => Client.ClearStatsAsync(ct);
}

/// <summary>Включить или выключить сбор статистики на машине с сервером.</summary>
public sealed record StatsToggle(bool Enabled);

public sealed record BackupNameRequest(string Name);

/// <summary>Ответ агента на проверку папки для копий: null — можно складывать.</summary>
public sealed record BackupDirCheck(string? Error);

/// <summary>serverconfig.json удалённого сервера: текст и отметка версии (время записи и длина) — по ней агент узнаёт,
/// не поменялся ли файл с тех пор, как окно его читало.</summary>
public sealed record RemoteConfigFile(bool Exists, string Text, string Stamp, DateTime? ChangedAt, bool WorldExists);

/// <summary>Сохранить конфиг удалённого сервера. Force — перезаписать, даже если файл на сервере успел измениться.</summary>
public sealed record ConfigSaveRequest(string Text, string ExpectedStamp, bool Force);

/// <summary>Conflict — файл на сервере уже другой (ничего не записано), File — каким он стал.</summary>
public sealed record ConfigSaveResult(bool Conflict, RemoteConfigFile File);

/// <summary>
/// Удаление в Корзину без диалогов: агент работает без окна, и вопрос на экране повесил бы запрос. На Linux — корзина
/// freedesktop.org в домашней папке пользователя (<see cref="Platform.FreedesktopTrash"/>), удалённое так же можно вернуть.
/// </summary>
public static class RecycleBin
{
    public static void Send(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                Platform.FreedesktopTrash.Send(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(Loc.T("sched.trashFailed", path, ex.Message), ex);
            }
            return;
        }

        var op = new ShFileOpStruct
        {
            wFunc = 3, // FO_DELETE
            pFrom = Path.GetFullPath(path) + "\0\0",
            // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
            fFlags = 0x0040 | 0x0010 | 0x0004 | 0x0400,
        };
        var result = SHFileOperation(ref op);
        if (result != 0 || op.fAnyOperationsAborted) throw new IOException(Loc.T("sched.recycleFailed", path, result));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct op);
}

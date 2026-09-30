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
    /// <summary>Путь к файлу — только для сервера на этой машине (для «Показать в папке»).</summary>
    [JsonIgnore]
    public string? LocalPath { get; init; }
}

/// <summary>Итог восстановления: куда отложен прежний мир (null — его не было).</summary>
public sealed record RestoreResult(string? SafetyName);

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
}

/// <summary>
/// Резервные копии и настройки расписания серверного профиля на этой машине. Этим пользуются и окно (свой сервер),
/// и агент (отвечая удалённому окну), поэтому логика одна.
/// </summary>
public sealed class ServerFiles(string profileId, string dataDir, string? backupPrefix, string? agentsDir = null)
{
    public string DataDir => dataDir;
    public string BackupsDir => new BackupStore(dataDir).Dir;

    private BackupStore Store => new(dataDir, backupPrefix);

    public ServerAutomation LoadAutomation() => ServerAutomation.Load(profileId, agentsDir);
    public void SaveAutomation(ServerAutomation settings) => settings.Save(profileId, agentsDir);

    public IReadOnlyList<BackupEntry> ListBackups() =>
        [.. Store.List().Select(b => new BackupEntry(b.Name, b.Time, b.Size, b.IsOwn) { LocalPath = b.Path })];

    public BackupEntry CopyWorld(DateTime now)
    {
        var save = SaveFileOf(dataDir) ?? throw new InvalidOperationException(Loc.T("sched.noConfig"));
        var store = Store;
        var made = store.CopySave(save, now);
        var settings = LoadAutomation();
        if (settings.BackupEnabled && settings.BackupKeep > 0) store.Prune(settings.BackupKeep);
        return new BackupEntry(made.Name, made.Time, made.Size, made.IsOwn) { LocalPath = made.Path };
    }

    public RestoreResult Restore(string name, DateTime now)
    {
        var store = Store;
        // имя — только из списка копий: путь «..\..\что-то» сюда не пройдёт
        var backup = store.Find(name) ?? throw new FileNotFoundException(Loc.T("sched.noBackup", name));
        var save = SaveFileOf(dataDir) ?? throw new InvalidOperationException(Loc.T("sched.noConfig"));
        return new RestoreResult(store.Restore(backup, save, now)?.Name);
    }

    public void DeleteBackup(string name)
    {
        var backup = Store.Find(name) ?? throw new FileNotFoundException(Loc.T("sched.noBackup", name));
        RecycleBin.Send(backup.Path);
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
public sealed class LocalServerData(ServerFiles files) : IServerData
{
    public ServerFiles Files => files;
    public bool IsRemote => false;
    public Task<ServerAutomation> LoadAutomationAsync(CancellationToken ct = default) => Task.Run(files.LoadAutomation, ct);
    public Task SaveAutomationAsync(ServerAutomation settings, CancellationToken ct = default) => Task.Run(() => files.SaveAutomation(settings), ct);
    public Task<IReadOnlyList<BackupEntry>> ListBackupsAsync(CancellationToken ct = default) => Task.Run(files.ListBackups, ct);
    public Task<BackupEntry> CopyWorldAsync(CancellationToken ct = default) => Task.Run(() => files.CopyWorld(DateTime.Now), ct);
    public Task<RestoreResult> RestoreAsync(string name, CancellationToken ct = default) => Task.Run(() => files.Restore(name, DateTime.Now), ct);
    public Task DeleteBackupAsync(string name, CancellationToken ct = default) => Task.Run(() => files.DeleteBackup(name), ct);
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
}

public sealed record BackupNameRequest(string Name);

/// <summary>Удаление в Корзину без диалогов: агент работает без окна, и вопрос на экране повесил бы запрос.</summary>
public static class RecycleBin
{
    public static void Send(string path)
    {
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

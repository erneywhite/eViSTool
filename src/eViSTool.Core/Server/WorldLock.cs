using eViSTool.Core.Localization;

namespace eViSTool.Core.Server;

/// <summary>Мир занят: сервер работает или идёт операция с файлами мира (восстановление, копия, запись конфига).</summary>
public sealed class WorldBusyException(string message) : InvalidOperationException(message);

/// <summary>
/// Замок на мир профиля — файл «.evistool-world.lock» в папке данных, открытый монопольно. Работает между процессами
/// (окно, агент) и внутри одного: сервер держит его всё время работы, восстановление, копия и запись конфига — на время
/// операции. Проверка «свободно» и бронь — одно действие, поэтому сервер не стартует посреди восстановления
/// (в том числе по сторожу), а восстановление не начнётся на запускающемся сервере. Процесс умер — замок отпущен.
/// В файле записано, кто держит: «server», «restore»… — чтобы отказ был понятным.
/// На Linux .NET запирает файлы через flock, и FileShare.Read там — общая блокировка, которую получил бы и второй
/// «держатель». Поэтому там замок открывается монопольно (FileShare.None), а кто держит — пишется в соседний файл:
/// монопольно открытый замок другим не прочитать.
/// </summary>
public sealed class WorldLock : IDisposable
{
    public const string FileName = ".evistool-world.lock";
    /// <summary>Кто держит мир — только на Linux, см. описание класса.</summary>
    public const string HolderFileName = ".evistool-world.holder";
    public const string Server = "server";
    public const string Restore = "restore";
    public const string Copy = "copy";
    public const string Config = "config";

    private readonly FileStream _file;

    private WorldLock(FileStream file) => _file = file;

    public static string PathFor(string dataDir) => System.IO.Path.Combine(dataDir, FileName);

    private static string HolderPathFor(string dataDir) =>
        OperatingSystem.IsWindows() ? PathFor(dataDir) : System.IO.Path.Combine(dataDir, HolderFileName);

    /// <summary>Занять мир. Уже занят — null.</summary>
    public static WorldLock? TryTake(string dataDir, string holder)
    {
        Directory.CreateDirectory(dataDir);
        var path = PathFor(dataDir);
        FileStream file;
        try
        {
            // другим можно только прочитать, кто держит; второй «держатель» (запись) получит отказ.
            // На Linux — монопольно: иначе flock общий и отказа не будет
            var share = OperatingSystem.IsWindows() ? FileShare.Read : FileShare.None;
            file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, share);
        }
        catch (IOException)
        {
            return null;
        }
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            file.SetLength(0);
            var bytes = System.Text.Encoding.UTF8.GetBytes(holder);
            file.Write(bytes);
            file.Flush();
            if (!OperatingSystem.IsWindows()) File.WriteAllBytes(HolderPathFor(dataDir), bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // подпись — для понятного отказа другим; сама бронь уже есть
        }
        return new WorldLock(file);
    }

    /// <summary>Занять мир или отказать понятным сообщением, кто его держит.</summary>
    public static WorldLock Take(string dataDir, string holder) =>
        TryTake(dataDir, holder) ?? throw new WorldBusyException(BusyMessage(dataDir));

    /// <summary>Кто держит мир (null — никто или не прочитать).</summary>
    public static string? HolderOf(string dataDir)
    {
        try
        {
            using var file = new FileStream(HolderPathFor(dataDir), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            return reader.ReadToEnd().Trim() is { Length: > 0 } text ? text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string BusyMessage(string dataDir) => HolderOf(dataDir) switch
    {
        Server => Loc.T("lock.server"),
        Restore => Loc.T("lock.restore"),
        Copy => Loc.T("lock.copy"),
        Config => Loc.T("lock.config"),
        _ => Loc.T("lock.other"),
    };

    public void Dispose() => _file.Dispose();
}

using System.Security.Cryptography;
using System.Text;
using eViSTool.Core.Localization;

namespace eViSTool.Core.Profiles;

/// <summary>
/// Короткий замок на файл конфига (serverconfig.json, clientsettings.json) на время «прочитать — поменять — записать».
/// Окно, агент и редактор пишут один и тот же файл: без замка двое, прочитавшие его разом, затёрли бы правку друг
/// друга (выключили мод — и тут же сохранили конфиг, в котором он ещё включён). Это не замок мира (<see cref="Server.WorldLock"/>):
/// работающий сервер держит тот всё время, а менять список модов при нём можно.
/// Замок — монопольно открытый файл во временной папке системы, имя — по полному пути конфига: в папке данных игры
/// ничего лишнего не появляется. Окно и агент на одной машине работают от одного пользователя и видят один замок.
/// Процесс умер — замок отпущен. Повторно не входится.
/// </summary>
public sealed class ConfigFileLock : IDisposable
{
    private readonly FileStream _file;

    private ConfigFileLock(FileStream file) => _file = file;

    public static string PathFor(string configPath)
    {
        var full = Path.GetFullPath(configPath);
        if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant(); // «C:\VS» и «c:\vs» — один файл
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..24];
        return Path.Combine(Path.GetTempPath(), "eViSTool-locks", id + ".lock");
    }

    /// <summary>Занять; занят — подождать до <paramref name="wait"/> (по умолчанию 5 с), не дождались — IOException.</summary>
    public static ConfigFileLock Take(string configPath, TimeSpan? wait = null)
    {
        var path = PathFor(configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var until = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            try
            {
                return new ConfigFileLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(50);
            }
            catch (IOException ex)
            {
                throw new IOException(Loc.T("cfg.busy", Path.GetFileName(configPath)), ex);
            }
        }
    }

    public void Dispose() => _file.Dispose();
}

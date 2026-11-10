using System.IO.Compression;
using eViSTool.Core.Localization;

namespace eViSTool.Core.Mods;

/// <summary>Мод, найденный в папке Mods.</summary>
public sealed record LocalMod(string Path, ModInfo? Info, string? Error)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path)!;
    public bool IsIdentified => Info is not null;

    /// <summary>Выключен в настройках игры (disabledMods / WorldConfig.DisabledMods).</summary>
    public bool IsDisabled { get; init; }

    /// <summary>Мод самого хостера (у G-Portal — папка gportal_mods): показываем, но не обновляем, не удаляем, не выключаем.</summary>
    public bool IsHosterMod { get; init; }

    /// <summary>
    /// Файл — релиз ModDB, у которого версия в modinfo.json не та, что на ModDB (<see cref="ReleaseMarks"/>): проверка
    /// обновлений сравнивает такой мод по версии релиза. null — отметки нет, сравнение по modinfo.
    /// </summary>
    public ReleaseMark? Release { get; init; }

    /// <summary>
    /// Отметка файла мода на диске этой машины — по ней окно узнаёт, что иконку (<see cref="ModIcon"/>) пора прочитать
    /// заново. Ставит сканер папки; у модов удалённого сервера и сервера у хостера её нет: их файлов здесь нет.
    /// </summary>
    public ModFileStamp? Stamp { get; init; }
}

/// <summary>
/// Файл мода на диске этой машины: путь, время изменения и размер. Другая отметка у того же пути — другой файл (мод
/// обновили на месте).
/// </summary>
public sealed record ModFileStamp(string Path, DateTime WriteTimeUtc, long Length);

/// <summary>Сканирует папку модов: zip-архивы и распакованные папки с modinfo.json.</summary>
public static class ModScanner
{
    private const string ModInfoFile = "modinfo.json";

    /// <summary>Временный файл установки мода рядом с будущим (<see cref="ModInstaller.Apply"/>).</summary>
    public const string TmpSuffix = ".evistool.tmp";

    /// <summary>Временный файл установки старше этого — остаток оборванной установки, а не идущая сейчас.</summary>
    public static readonly TimeSpan StaleTmpAge = TimeSpan.FromHours(1);

    /// <summary>
    /// Остаток оборванной установки (окно закрыли с «Да», процесс убили, выключили свет) — удалить: игра его не читает,
    /// а на диске он лежал бы вечно, размером с мод. Возраст на Windows — по самой поздней из дат создания и изменения:
    /// копия там ставит файлу дату изменения исходника, и идущая сейчас установка выглядела бы старой. На Linux копия
    /// дату изменения обновляет по ходу записи, а дату создания .NET там знает не везде — хватает даты изменения.
    /// Файл, который ещё пишут, Windows удалить не даст — тогда просто пропускаем.
    /// </summary>
    internal static bool DeleteIfStale(FileInfo info, DateTime nowUtc)
    {
        var touched = OperatingSystem.IsWindows() && info.CreationTimeUtc > info.LastWriteTimeUtc
            ? info.CreationTimeUtc : info.LastWriteTimeUtc;
        if (nowUtc - touched < StaleTmpAge) return false;
        try
        {
            info.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Сканирует несколько папок; несуществующие пропускает.</summary>
    public static IReadOnlyList<LocalMod> Scan(IEnumerable<string> modsDirs) =>
        modsDirs.Where(System.IO.Directory.Exists)
            .SelectMany(Scan)
            .OrderBy(m => m.Info?.Name ?? m.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IReadOnlyList<LocalMod> Scan(string modsDir)
    {
        if (!System.IO.Directory.Exists(modsDir))
            throw new DirectoryNotFoundException(Loc.T("err.modsDirNotFound", modsDir));

        var result = new List<LocalMod>();
        // время и размер приходят вместе с перечнем папки, отдельно файлы не опрашиваем. Путь — как у
        // Directory.EnumerateFiles (папка, как её дали, плюс имя): по нему мод узнают история и отметки релизов
        var folder = new DirectoryInfo(modsDir);

        foreach (var info in folder.EnumerateFiles())
        {
            if (info.Name.EndsWith(TmpSuffix, StringComparison.OrdinalIgnoreCase))
            {
                DeleteIfStale(info, DateTime.UtcNow);
                continue;
            }
            var file = System.IO.Path.Combine(modsDir, info.Name);
            var stamp = new ModFileStamp(file, info.LastWriteTimeUtc, info.Length);
            var ext = info.Extension;
            if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                result.Add(ReadZip(file) with { Stamp = stamp });
            else if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
                result.Add(new LocalMod(file, null, Loc.T("scan.codeModUnsupported")) { Stamp = stamp });
            // остальное (json-файлы менеджеров, архивы .rar и т. п.) игра не грузит — пропускаем
        }

        foreach (var info in folder.EnumerateDirectories())
        {
            var dir = System.IO.Path.Combine(modsDir, info.Name);
            var infoPath = System.IO.Path.Combine(dir, ModInfoFile);
            if (!File.Exists(infoPath)) continue;
            // shortcut: у папки — её собственное время, а правка modicon.png внутри его не меняет; новая иконка такого мода
            // видна со следующего запуска. Папками ставят моды их авторы при разработке — переделать, если начнут жаловаться
            result.Add(Read(dir, () => File.ReadAllText(infoPath)) with { Stamp = new ModFileStamp(dir, info.LastWriteTimeUtc, 0) });
        }

        return result.OrderBy(m => m.Info?.Name ?? m.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static LocalMod ReadZip(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(ModInfoFile, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                var nested = zip.Entries.Any(e => e.Name.Equals(ModInfoFile, StringComparison.OrdinalIgnoreCase));
                return new LocalMod(zipPath, null, nested
                    ? Loc.T("scan.nestedModInfo")
                    : Loc.T("scan.noModInfo"));
            }

            var json = Platform.BoundedRead.Text(entry); // предел: архив-«бомба» не съест память
            return Read(zipPath, () => json);
        }
        catch (InvalidDataException)
        {
            return new LocalMod(zipPath, null, Loc.T("scan.corrupt"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LocalMod(zipPath, null, Loc.T("scan.readFailed", ex.Message));
        }
    }

    /// <summary>Мод по тексту modinfo.json (архив прочитан не с диска — например, с сервера у хостера): те же проверки
    /// modid, что у сканера папки.</summary>
    public static LocalMod FromModInfo(string path, string json) => Read(path, () => json);

    /// <summary>Мод по записи кэша (<see cref="Hosting.ModInfoCache"/>): текст modinfo.json или ключ ошибки.
    /// Ключ пришёл из файла на диске, поэтому в Loc.T идут только известные ключи-литералы; незнакомый (кэш битый или
    /// записан более новой версией) — «архив повреждён», а не сырой ключ в окне.</summary>
    public static LocalMod FromCached(string path, Hosting.CachedModInfo cached) =>
        cached.ModInfoJson is { } json ? FromModInfo(path, json) : new LocalMod(path, null, cached.ErrorKey switch
        {
            "scan.noModInfo" => Loc.T("scan.noModInfo"),
            "scan.nestedModInfo" => Loc.T("scan.nestedModInfo"),
            _ => Loc.T("scan.corrupt"),
        });

    private static LocalMod Read(string path, Func<string> readJson)
    {
        try
        {
            var info = ModInfo.Parse(readJson());
            if (string.IsNullOrEmpty(info.ModId)) return new LocalMod(path, info, Loc.T("scan.noModId"));
            // modid идёт в пути (хранилище версий, история) — такой мод не трогаем, только показываем
            return PathRules.IsSafeModId(info.ModId)
                ? new LocalMod(path, info, null)
                : new LocalMod(path, null, Loc.T("scan.badModId", info.OriginalModId));
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            return new LocalMod(path, null, Loc.T("scan.badModInfo", ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // modinfo.json мода-папки не прочитать (занят, нет прав) — этот мод с ошибкой, а не обрыв всего сканирования
            return new LocalMod(path, null, Loc.T("scan.readFailed", ex.Message));
        }
    }
}

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
}

/// <summary>Сканирует папку модов: zip-архивы и распакованные папки с modinfo.json.</summary>
public static class ModScanner
{
    private const string ModInfoFile = "modinfo.json";

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

        foreach (var file in System.IO.Directory.EnumerateFiles(modsDir))
        {
            var ext = System.IO.Path.GetExtension(file);
            if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                result.Add(ReadZip(file));
            else if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
                result.Add(new LocalMod(file, null, Loc.T("scan.codeModUnsupported")));
            // остальное (json-файлы менеджеров, архивы .rar и т. п.) игра не грузит — пропускаем
        }

        foreach (var dir in System.IO.Directory.EnumerateDirectories(modsDir))
        {
            var infoPath = System.IO.Path.Combine(dir, ModInfoFile);
            if (!File.Exists(infoPath)) continue;
            result.Add(Read(dir, () => File.ReadAllText(infoPath)));
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

            using var reader = new StreamReader(entry.Open());
            var json = reader.ReadToEnd();
            return Read(zipPath, () => json);
        }
        catch (InvalidDataException)
        {
            return new LocalMod(zipPath, null, Loc.T("scan.corrupt"));
        }
        catch (IOException ex)
        {
            return new LocalMod(zipPath, null, Loc.T("scan.readFailed", ex.Message));
        }
    }

    private static LocalMod Read(string path, Func<string> readJson)
    {
        try
        {
            var info = ModInfo.Parse(readJson());
            return string.IsNullOrEmpty(info.ModId)
                ? new LocalMod(path, info, Loc.T("scan.noModId"))
                : new LocalMod(path, info, null);
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            return new LocalMod(path, null, Loc.T("scan.badModInfo", ex.Message));
        }
    }
}

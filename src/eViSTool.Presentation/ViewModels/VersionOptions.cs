using System.IO;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Вариант для отката: сохранённая копия (Path) или релиз из модбазы (Release).</summary>
public sealed record VersionOption(string Version, string Source, string Date, string Note, string? Path, ModDbRelease? Release);

/// <summary>Список версий мода для отката и для вкладки «Версии».</summary>
public static class VersionOptions
{
    /// <summary>Собирает варианты: сначала сохранённые копии, потом модбаза.</summary>
    public static List<VersionOption> Build(ModBackupStore? store, string modId, string? installedVersion,
        IReadOnlyList<ModDbRelease> releases)
    {
        var list = new List<VersionOption>();
        foreach (var path in store?.List(modId) ?? [])
        {
            var info = File.Exists(path) ? ModScanner.ReadZip(path).Info : null;
            var version = info?.Version ?? System.IO.Path.GetFileName(path);
            list.Add(new VersionOption(version, Loc.T("rollback.savedCopy"), Dates.Stamp(File.GetCreationTime(path)),
                version == installedVersion ? Loc.T("rollback.installed") : "", path, null));
        }
        foreach (var r in releases)
        {
            var date = DateTime.TryParse(r.Created, out var d) ? Dates.Day(d) : r.Created ?? "";
            list.Add(new VersionOption(r.ModVersion ?? "?", "ModDB", date,
                r.ModVersion == installedVersion ? Loc.T("rollback.installed") : "", null, r));
        }
        return list;
    }
}

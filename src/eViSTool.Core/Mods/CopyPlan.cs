using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Versioning;

namespace eViSTool.Core.Mods;

/// <summary>Мод, который встанет в другой профиль; <see cref="Replaces"/> — версия, что там стоит сейчас (null — мода там нет).</summary>
public sealed record CopyItem(LocalMod Mod, string? Replaces);

/// <summary>
/// «Скопировать в профиль»: что из выбранных модов поставить в другой профиль. Ставится то, чего там нет или что там
/// старее. Не ставится: клиентский мод на сервер (выделенный сервер его не загружает), мод-папка (поставить нечем — не
/// архив) и мод, который там уже такой же или новее, — копия не должна молча понижать версию.
/// </summary>
public sealed record CopyPlan(
    IReadOnlyList<CopyItem> Install,
    IReadOnlyList<LocalMod> ClientOnly,
    IReadOnlyList<LocalMod> Folders,
    IReadOnlyList<(LocalMod Mod, string Theirs)> NotOlder)
{
    /// <param name="mods">Выбранные моды (с modinfo).</param>
    /// <param name="there">Моды целевого профиля.</param>
    /// <param name="kind">Вид целевого профиля.</param>
    /// <param name="isFolder">Мод — папка, а не архив.</param>
    public static CopyPlan For(IReadOnlyList<LocalMod> mods, IReadOnlyList<LocalMod> there, ProfileKind kind, Func<LocalMod, bool> isFolder)
    {
        var theirs = there.Where(l => l.Info is not null)
            .GroupBy(l => l.Info!.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Info!.Version ?? "", StringComparer.OrdinalIgnoreCase);
        var install = new List<CopyItem>();
        var clientOnly = new List<LocalMod>();
        var folders = new List<LocalMod>();
        var notOlder = new List<(LocalMod, string)>();
        foreach (var mod in mods.Where(m => m.Info is not null))
        {
            var info = mod.Info!;
            if (ModSides.IsUnneeded(ModSides.Parse(info.Side), kind)) clientOnly.Add(mod);
            else if (isFolder(mod)) folders.Add(mod);
            else if (!theirs.TryGetValue(info.ModId, out var version)) install.Add(new CopyItem(mod, null));
            else if (IsNewer(info.Version, version)) install.Add(new CopyItem(mod, version));
            else notOlder.Add((mod, version));
        }
        return new CopyPlan(install, clientOnly, folders, notOlder);
    }

    /// <summary>Наша версия новее той, что в цели. Версию не разобрать — не новее: лучше не тронуть, чем понизить.</summary>
    private static bool IsNewer(string? ours, string theirs) =>
        ModVersion.ParseOrNull(ours) is { } a && ModVersion.ParseOrNull(theirs) is { } b && a.CompareTo(b) > 0;

    /// <summary>Строки о пропущенном — к вопросу перед копированием и в итог.</summary>
    public List<string> Notes(Func<LocalMod, string> name)
    {
        var lines = new List<string>();
        if (ClientOnly.Count > 0) lines.Add(Loc.T("multi.copyClientSkipped", ClientOnly.Count, string.Join(", ", ClientOnly.Select(name))));
        if (Folders.Count > 0) lines.Add(Loc.T("multi.copyFolders", string.Join(", ", Folders.Select(name))));
        if (NotOlder.Count > 0)
            lines.Add(Loc.T("multi.copyNotOlder", NotOlder.Count, string.Join(", ", NotOlder.Select(n => $"{name(n.Mod)} {n.Theirs}"))));
        return lines;
    }
}

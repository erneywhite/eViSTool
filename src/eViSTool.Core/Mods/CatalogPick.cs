using eViSTool.Core.ModDb;
using eViSTool.Core.Versioning;

namespace eViSTool.Core.Mods;

/// <summary>Насколько выбранный релиз подходит профилю.</summary>
public enum CatalogFit
{
    /// <summary>Стабильный релиз для ветки игры профиля.</summary>
    Compatible,
    /// <summary>Для ветки игры есть только предварительный (или профиль уже на предварительном).</summary>
    CompatiblePrerelease,
    /// <summary>Для ветки игры релиза нет.</summary>
    NoneForBranch,
    /// <summary>Версия игры профиля неизвестна: совместимость не проверена.</summary>
    UnknownGame,
}

public sealed record CatalogChoice(ModDbRelease? Release, CatalogFit Fit, ModDbRelease? Latest);

/// <summary>
/// Какой релиз предложить в карточке каталога для версии игры профиля. Неизвестная версия — последний стабильный
/// (предварительный — только если стабильных нет вовсе), и он не выдаётся за проверенно совместимый.
/// </summary>
public static class CatalogPick
{
    public static CatalogChoice Best(IReadOnlyList<ModDbRelease> releases, ModVersion? game, bool onPrerelease = false)
    {
        var latest = UpdateChecker.PickLatest(releases, allowUnstable: true);
        if (game is null)
            return new CatalogChoice(UpdateChecker.PickLatest(releases, allowUnstable: false) ?? latest, CatalogFit.UnknownGame, latest);

        // как во вкладке «Моды»: стоит пре-релиз — значит, сознательно на нестабильной ветке мода
        var best = UpdateChecker.PickLatestCompatible(releases, game, allowUnstable: onPrerelease)
                   ?? UpdateChecker.PickLatestCompatible(releases, game, allowUnstable: true);
        if (best is null) return new CatalogChoice(null, CatalogFit.NoneForBranch, latest);
        var pre = ModVersion.ParseOrNull(best.ModVersion)?.IsPrerelease == true;
        return new CatalogChoice(best, pre ? CatalogFit.CompatiblePrerelease : CatalogFit.Compatible, latest);
    }

    /// <summary>Релиз отмечен для ветки этой версии игры (неизвестная версия — проверить нечем: false).</summary>
    public static bool Fits(ModDbRelease release, ModVersion? game) =>
        game is not null && UpdateChecker.CompatibleReleases([release], game).Count > 0;
}

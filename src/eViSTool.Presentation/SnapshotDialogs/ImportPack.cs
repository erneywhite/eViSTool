using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Packs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>Подтверждение импорта модпака (dialog.importpack, dialog.importpack.plain).</summary>
internal static class ImportPack
{
    /// <summary>Главное состояние: описание, предупреждение о версии игры, все действия, режим «как в паке», настройки модов.</summary>
    [SnapshotDialog("importpack")]
    private static void Open(MainViewModel main) => AppWindows.Current.ConfirmPackImport(Plan(withExtras: true));

    /// <summary>Без описания, предупреждения и настроек модов; моды профиля все в паке — режим «как в паке» недоступен.</summary>
    [SnapshotDialog("importpack.plain")]
    private static void OpenPlain(MainViewModel main) => AppWindows.Current.ConfirmPackImport(Plan(withExtras: false));

    private static PackImportPlan Plan(bool withExtras)
    {
        var manifest = new PackManifest
        {
            Name = "Сервер друзей — осень",
            Description = withExtras ? "Сборка для нашего сервера: еда, выживание, рюкзаки и пара мелочей. Настройки подобраны под 1.20." : null,
            CreatedWith = "eViSTool 0.10.5",
            Created = new DateTime(2026, 10, 9, 18, 30, 0, DateTimeKind.Utc),
            GameVersion = "1.20.7",
            IncludesModConfig = withExtras,
            Mods =
            [
                Mod("primitivesurvival", "Primitive Survival", "3.8.4"),
                Mod("carryon", "Carry On", "1.8.0"),
                Mod("expandedfoods", "Expanded Foods", "1.6.2"),
                Mod("hydrateordiedrate", "Hydrate Or Diedrate", "2.3.0", bundled: true),
                Mod("betterruins", "Better Ruins", "0.4.11", bundled: true, enabled: false),
                Mod("carryon", "Carry On", "1.7.0"), // тот же мод второй раз
            ],
        };

        // что стоит у меня: Primitive Survival старее, Expanded Foods той же версии, Hydrate Or Diedrate новее
        var primitive = Local("primitivesurvival", "Primitive Survival", "3.8.1");
        var foods = Local("expandedfoods", "Expanded Foods", "1.6.2");
        var hydrate = Local("hydrateordiedrate", "Hydrate Or Diedrate", "2.4.0");
        var items = new List<PackItemPlan>
        {
            new(manifest.Mods[0], primitive, PackItemAction.Update),
            new(manifest.Mods[1], null, PackItemAction.Install),
            new(manifest.Mods[2], foods, PackItemAction.Same),
            new(manifest.Mods[3], hydrate, PackItemAction.Downgrade),
            new(manifest.Mods[4], null, PackItemAction.Install),
            new(manifest.Mods[5], null, PackItemAction.Duplicate),
        };

        // моды профиля, которых в паке нет, — режим «как в паке» их выключит
        IReadOnlyList<LocalMod> notInPack = withExtras
            ? [Local("automapmarkers", "Auto Map Markers", "1.2.0"), Local("xskills", "XSkills", "0.9.3")]
            : [];
        var warning = withExtras ? Loc.T("pack.gameMismatch", manifest.GameVersion, "1.21.1") : null;
        return new PackImportPlan(manifest, items, notInPack, warning);
    }

    private static PackMod Mod(string id, string name, string version, bool bundled = false, bool enabled = true) =>
        new() { ModId = id, Name = name, Version = version, Enabled = enabled, Bundled = bundled, FileName = $"{id}_{version}.zip", Sha256 = "" };

    private static LocalMod Local(string id, string name, string version) =>
        new($@"C:\VintagestoryData\Mods\{id}_{version}.zip", new ModInfo { ModId = id, OriginalModId = id, Name = name, Version = version }, null);
}

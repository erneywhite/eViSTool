using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Packs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>Окно импорта модпака: строки «что будет с каждым модом», сведения о паке, режим и настройки модов.</summary>
public sealed class ImportPackViewModelTests
{
    private static PackMod PackMod(string name, string? version = "1.0.0", bool bundled = false, bool enabled = true) =>
        new() { ModId = name.ToLowerInvariant().Replace(' ', '_'), Name = name, Version = version, Bundled = bundled, Enabled = enabled, FileName = name + ".zip" };

    // папка модов — путём этой системы: на Linux «C:\Mods\x.zip» не делится на папку и имя файла
    private static readonly string ModsDir = Path.Combine(Path.GetTempPath(), "Mods");

    private static LocalMod Local(string name, string version = "1.0.0", string? file = null) =>
        new(Path.Combine(ModsDir, file ?? name + ".zip"), new ModInfo { ModId = name.ToLowerInvariant(), Name = name, Version = version }, null);

    private static PackImportPlan Plan(IReadOnlyList<PackItemPlan> items, IReadOnlyList<LocalMod>? notInPack = null,
        Action<PackManifest>? tune = null, string? warning = null)
    {
        var manifest = new PackManifest { Name = "Сервер друзей", Mods = [.. items.Select(i => i.Mod)] };
        tune?.Invoke(manifest);
        return new PackImportPlan(manifest, items, notInPack ?? [], warning);
    }

    private static PackItemPlan Item(PackMod mod, PackItemAction action, LocalMod? installed = null) => new(mod, installed, action);

    [Fact]
    public void Items_ChangesFirst_ThenByNameIgnoringCase()
    {
        var plan = Plan([
            Item(PackMod("zebra"), PackItemAction.Same, Local("zebra")),
            Item(PackMod("Beta"), PackItemAction.Update, Local("Beta", "0.9.0")),
            Item(PackMod("alpha"), PackItemAction.Duplicate),
            Item(PackMod("Gamma"), PackItemAction.Install),
            Item(PackMod("delta"), PackItemAction.Downgrade, Local("delta", "2.0.0")),
        ]);

        var rows = new ImportPackViewModel(plan).Items;

        // изменяемые — по имени без учёта регистра, потом «уже стоит» и повторы — тоже по имени
        Assert.Equal(["Beta", "delta", "Gamma", "alpha", "zebra"], rows.Select(r => r.Name));
    }

    [Fact]
    public void Items_ShowVersionsActionAndSource()
    {
        var plan = Plan([
            Item(PackMod("Fresh"), PackItemAction.Install),
            Item(PackMod("Upgrade", "2.0.0", bundled: true), PackItemAction.Update, Local("Upgrade", "1.5.0")),
            Item(PackMod("Older", "1.0.0"), PackItemAction.Downgrade, Local("Older", "1.2.0")),
            Item(PackMod("Stays", "3.1.0", bundled: true), PackItemAction.Same, Local("Stays", "3.1.0")),
            Item(PackMod("Twice", "1.0.0"), PackItemAction.Duplicate),
        ]);

        var rows = new ImportPackViewModel(plan).Items.ToDictionary(r => r.Name);

        Assert.Equal(("1.0.0", "—", Loc.T("packi.actInstall"), Loc.T("packi.srcModDb")),
            (rows["Fresh"].PackVersion, rows["Fresh"].MineVersion, rows["Fresh"].ActionText, rows["Fresh"].SourceText));
        Assert.Equal(("2.0.0", "1.5.0", Loc.T("packi.actUpdate"), Loc.T("packi.srcInside")),
            (rows["Upgrade"].PackVersion, rows["Upgrade"].MineVersion, rows["Upgrade"].ActionText, rows["Upgrade"].SourceText));
        Assert.Equal(("1.0.0", "1.2.0", Loc.T("packi.actDowngrade"), Loc.T("packi.srcModDb")),
            (rows["Older"].PackVersion, rows["Older"].MineVersion, rows["Older"].ActionText, rows["Older"].SourceText));
        // что не меняется — без источника: ничего не качается и не берётся из пака
        Assert.Equal((Loc.T("packi.actSame"), ""), (rows["Stays"].ActionText, rows["Stays"].SourceText));
        Assert.Equal((Loc.T("packi.actDuplicate"), ""), (rows["Twice"].ActionText, rows["Twice"].SourceText));
    }

    [Fact]
    public void Items_DisabledModIsMarked_UnknownVersionIsQuestionMark()
    {
        var plan = Plan([Item(PackMod("Quiet", version: null, enabled: false), PackItemAction.Install)]);

        var row = Assert.Single(new ImportPackViewModel(plan).Items);

        Assert.Equal("Quiet " + Loc.T("packi.disabledMark"), row.Name);
        Assert.Equal("?", row.PackVersion);
    }

    [Fact]
    public void Summary_CountsChangesAndDownloads()
    {
        var plan = Plan([
            Item(PackMod("A"), PackItemAction.Install),                       // скачать
            Item(PackMod("B", bundled: true), PackItemAction.Update, Local("B", "0.5.0")), // из пака — не скачивается
            Item(PackMod("C"), PackItemAction.Downgrade, Local("C", "2.0.0")), // скачать
            Item(PackMod("D"), PackItemAction.Same, Local("D")),              // не меняется
            Item(PackMod("E"), PackItemAction.Duplicate),                     // пропущен
        ]);

        Assert.Equal(Loc.T("packi.summary", 3, 2), new ImportPackViewModel(plan).Summary);
    }

    [Fact]
    public void Header_ShowsNameInfoDescriptionAndWarning()
    {
        var created = new DateTime(2026, 10, 9, 18, 30, 0, DateTimeKind.Utc);
        var plan = Plan([Item(PackMod("A"), PackItemAction.Install), Item(PackMod("B"), PackItemAction.Install)],
            tune: m =>
            {
                m.Description = "Для друзей";
                m.GameVersion = "1.20.7";
                m.CreatedWith = "eViSTool 0.10.5";
                m.Created = created;
            }, warning: "другая версия игры");

        var model = new ImportPackViewModel(plan);

        Assert.Equal("Сервер друзей", model.PackName);
        Assert.Equal(Loc.T("packi.info", 2, "1.20.7", Dates.Day(created.ToLocalTime()), "eViSTool 0.10.5"), model.Info);
        Assert.True(model.HasDescription);
        Assert.Equal("Для друзей", model.Description);
        Assert.True(model.HasWarning);
        Assert.Equal("другая версия игры", model.Warning);
    }

    [Fact]
    public void Header_NoDescriptionNoWarning_UnknownGameAndAuthorAreQuestionMarks()
    {
        var plan = Plan([Item(PackMod("A"), PackItemAction.Install)], tune: m => { m.Description = "   "; m.GameVersion = null; m.CreatedWith = null; });

        var model = new ImportPackViewModel(plan);

        Assert.False(model.HasDescription);
        Assert.False(model.HasWarning);
        Assert.Contains("?", model.Info);
        Assert.Equal(Loc.T("packi.info", 1, "?", Dates.Day(plan.Manifest.Created.ToLocalTime()), "?"), model.Info);
    }

    [Fact]
    public void Mirror_NeedsModsOutsideThePack_TipListsThem()
    {
        var items = new[] { Item(PackMod("A"), PackItemAction.Install) };
        // у одного мода нет modinfo (имени нет) — в подсказке имя файла
        var unknown = new LocalMod(Path.Combine(ModsDir, "mystery.zip"), null, "нет modinfo.json");
        var model = new ImportPackViewModel(Plan(items, [Local("Extra"), unknown]));

        Assert.True(model.CanMirror);
        Assert.Equal("Extra\nmystery.zip", model.MirrorTip);
        Assert.Equal(Loc.T("packi.modeMirror", 2), model.MirrorText);

        var none = new ImportPackViewModel(Plan(items));

        Assert.False(none.CanMirror);
        Assert.Null(none.MirrorTip);
        Assert.Equal(Loc.T("packi.modeMirror", 0), none.MirrorText);
    }

    [Fact]
    public void Choice_Defaults_AddModeAndConfigAppliedWhenPackHasIt()
    {
        var items = new[] { Item(PackMod("A"), PackItemAction.Install) };

        var withConfig = new ImportPackViewModel(Plan(items, [Local("Extra")], m => m.IncludesModConfig = true));
        Assert.True(withConfig.HasModConfig);
        Assert.Equal(new PackImportChoice(Mirror: false, ApplyConfig: true), withConfig.Choice);

        var without = new ImportPackViewModel(Plan(items, [Local("Extra")]));
        Assert.False(without.HasModConfig);
        Assert.Equal(new PackImportChoice(Mirror: false, ApplyConfig: false), without.Choice);
    }

    [Fact]
    public void Choice_FollowsTheSwitches()
    {
        var model = new ImportPackViewModel(Plan([Item(PackMod("A"), PackItemAction.Install)], [Local("Extra")], m => m.IncludesModConfig = true));

        model.Mirror = true;
        model.ApplyConfig = false;

        Assert.Equal(new PackImportChoice(Mirror: true, ApplyConfig: false), model.Choice);
    }

    [Fact]
    public void Choice_IgnoresSwitchesThatWereNotOnScreen()
    {
        // режима «как в паке» нет (выключать нечего) и настроек в паке нет — что бы ни стояло в свойствах, ответ чистый
        var model = new ImportPackViewModel(Plan([Item(PackMod("A"), PackItemAction.Install)]));

        model.Mirror = true;
        model.ApplyConfig = true;

        Assert.Equal(new PackImportChoice(Mirror: false, ApplyConfig: false), model.Choice);
    }

    [Fact]
    public void Switches_NotifyTheWindow()
    {
        var model = new ImportPackViewModel(Plan([Item(PackMod("A"), PackItemAction.Install)], [Local("Extra")]));
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.Mirror = true;
        model.ApplyConfig = true;

        Assert.Equal([nameof(ImportPackViewModel.Mirror), nameof(ImportPackViewModel.ApplyConfig)], changed);
    }
}

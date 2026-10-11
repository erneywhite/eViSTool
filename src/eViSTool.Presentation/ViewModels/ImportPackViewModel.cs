using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Core.Localization;
using eViSTool.Core.Packs;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Строка предпросмотра импорта: что будет с одним модом пака.</summary>
public sealed record PackItemRow(string Name, string PackVersion, string MineVersion, string ActionText, string SourceText);

/// <summary>
/// Предпросмотр импорта модпака (окно «Импорт модпака»): сведения о паке, предупреждение о версии игры, что будет с
/// каждым модом, режим («добавить к моим» или «сделать как в паке») и настройки модов. Ответ окна — <see cref="Choice"/>.
/// </summary>
public sealed partial class ImportPackViewModel : ObservableObject
{
    public ImportPackViewModel(PackImportPlan plan)
    {
        var m = plan.Manifest;

        PackName = m.Name;
        Info = Loc.T("packi.info", m.Mods.Count, m.GameVersion ?? "?", Dates.Day(m.Created.ToLocalTime()), m.CreatedWith ?? "?");
        Description = m.Description ?? "";
        Warning = plan.GameVersionWarning;

        Items = Rows(plan);

        MirrorText = Loc.T("packi.modeMirror", plan.NotInPack.Count);
        CanMirror = plan.NotInPack.Count > 0;
        MirrorTip = CanMirror ? string.Join("\n", plan.NotInPack.Select(l => l.Info?.Name ?? l.FileName)) : null;

        // настройки модов в паке: предложить применить, по умолчанию — да
        HasModConfig = m.IncludesModConfig;
        _applyConfig = HasModConfig;

        var changes = plan.Items.Count(Changes);
        var downloads = plan.Items.Count(i => Changes(i) && !i.Mod.Bundled);
        Summary = Loc.T("packi.summary", changes, downloads);
    }

    public string PackName { get; }

    /// <summary>«Модов: 6 · игра 1.20.7 · собран 09.10.2026 · eViSTool 0.10.5».</summary>
    public string Info { get; }

    public string Description { get; }
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    /// <summary>Пак собран для другой ветки игры, чем у профиля; null — всё в порядке.</summary>
    public string? Warning { get; }
    public bool HasWarning => Warning is not null;

    /// <summary>Что произойдёт с каждым модом: сначала то, что изменится, потом по имени.</summary>
    public IReadOnlyList<PackItemRow> Items { get; }

    /// <summary>«Сделать как в паке — … (N, не удаляются)».</summary>
    public string MirrorText { get; }

    /// <summary>Режим «как в паке» имеет смысл, только когда в профиле есть моды, которых в паке нет.</summary>
    public bool CanMirror { get; }

    /// <summary>Какие моды выключит режим «как в паке» (по одному в строке); null — выключать нечего.</summary>
    public string? MirrorTip { get; }

    /// <summary>В паке лежат настройки модов — показать галочку «Применить настройки».</summary>
    public bool HasModConfig { get; }

    public string Summary { get; }

    /// <summary>Режим «как в паке»: ещё и выключить моды профиля, которых в нём нет. Выключено — режим «добавить».</summary>
    [ObservableProperty] private bool _mirror;

    [ObservableProperty] private bool _applyConfig;

    /// <summary>Ответ окна. Режим и настройки учитываются, только если они были доступны на экране.</summary>
    public PackImportChoice Choice => new(CanMirror && Mirror, HasModConfig && ApplyConfig);

    private static bool Changes(PackItemPlan item) => item.Action is not (PackItemAction.Same or PackItemAction.Duplicate);

    private static List<PackItemRow> Rows(PackImportPlan plan) => plan.Items
        .OrderBy(i => !Changes(i)) // сначала то, что изменится
        .ThenBy(i => i.Mod.Name, StringComparer.OrdinalIgnoreCase)
        .Select(i => new PackItemRow(
            i.Mod.Name + (i.Mod.Enabled ? "" : " " + Loc.T("packi.disabledMark")),
            i.Mod.Version ?? "?",
            i.Installed?.Info?.Version ?? "—",
            Loc.T(i.Action switch
            {
                PackItemAction.Install => "packi.actInstall",
                PackItemAction.Update => "packi.actUpdate",
                PackItemAction.Downgrade => "packi.actDowngrade",
                PackItemAction.Duplicate => "packi.actDuplicate",
                _ => "packi.actSame",
            }),
            Changes(i) ? Loc.T(i.Mod.Bundled ? "packi.srcInside" : "packi.srcModDb") : ""))
        .ToList();
}

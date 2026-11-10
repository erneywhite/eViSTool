using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Presentation.ViewModels.Mods;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Пункт меню «Категория ▸»: перенести выбранный мод в эту категорию («» — без категории). Пункт с подписью «-» —
/// черта между «Без категории» и категориями (так её рисует меню Avalonia).
/// </summary>
public sealed record CategoryChoice(string Label, string Category, IRelayCommand? Command, bool IsCurrent = false)
{
    public bool IsSeparator => Label == "-";
}

/// <summary>
/// Свои категории модов: только для порядка в таблице (порядок загрузки модов в Vintage Story решают сами моды).
/// Хранятся в профиле на этой машине (<see cref="Core.Profiles.GameProfile.ModCategories"/>) — и у удалённого сервера,
/// и у сервера у хостера. С включённым «По категориям» таблица разбита на сворачиваемые группы.
/// </summary>
public sealed partial class ModsViewModel
{
    /// <summary>Таблица разбита на группы по категориям (настройка окна, общая для профилей).</summary>
    public bool GroupByCategory
    {
        get => _main.GroupModsByCategory;
        set
        {
            if (value == _main.GroupModsByCategory) return;
            _main.GroupModsByCategory = value;
            OnPropertyChanged();
            ApplySort();
        }
    }

    /// <summary>Категории профиля по алфавиту — для выбора в карточке и в меню.</summary>
    public ObservableCollection<string> Categories { get; } = [];

    /// <summary>Пункты меню «Категория ▸»: категории профиля и «Без категории».</summary>
    public ObservableCollection<CategoryChoice> CategoryMenu { get; } = [];

    /// <summary>Подпись группы модов без категории (она всегда последняя).</summary>
    public static string NoCategoryLabel => Loc.T("mods.noCategory");

    /// <summary>Подпись группы модов, для которых есть обновление (она всегда первая; своей категории мода не меняет).</summary>
    public static string UpdatesGroupLabel => Loc.T("mods.groupUpdates");

    /// <summary>Ключ мода в словаре категорий: modid, а у мода без modid — имя файла.</summary>
    private static string CategoryKey(ModRowViewModel row) => row.ModId.Length > 0 ? row.ModId : row.FileName;

    private Dictionary<string, string>? CategoryStore => _main.ActiveProfile?.Model.ModCategories;

    /// <summary>Категории профиля — строкам таблицы и в списки выбора (после перестройки таблицы и смены категорий).</summary>
    private void ApplyCategories()
    {
        var store = CategoryStore;
        foreach (var row in Rows) row.Category = store?.GetValueOrDefault(CategoryKey(row)) ?? "";
        RefreshCategoryLists();
    }

    private void RefreshCategoryLists()
    {
        var names = (CategoryStore?.Values ?? Enumerable.Empty<string>()).Where(c => c.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (!names.SequenceEqual(Categories))
        {
            Categories.Clear();
            foreach (var name in names) Categories.Add(name);
        }
        // «Без категории» — первым и отдельно: убрать из категорий, а не ещё одна категория. У каждой — сколько в ней модов
        CategoryMenu.Clear();
        // выбрано несколько — отмечена категория, только если она у всех одна
        var current = IsMulti
            ? SelectedRows.Select(r => r.Category).Distinct().Count() == 1 ? SelectedRows[0].Category : null
            : Selected?.Category ?? "";
        CategoryMenu.Add(new CategoryChoice(Labelled(NoCategoryLabel, Rows.Count(r => r.Category.Length == 0)), "",
            new RelayCommand(() => MoveSelected("")), current is { Length: 0 }));
        if (names.Count > 0) CategoryMenu.Add(new CategoryChoice("-", "", null));
        foreach (var name in names)
            CategoryMenu.Add(new CategoryChoice(Labelled(name, Rows.Count(r => r.Category == name)), name,
                new RelayCommand(() => MoveSelected(name)), current == name));
    }

    private static string Labelled(string name, int count) => Loc.T("catsuggest.row", name, count);

    /// <summary>Пункт «По категориям» в меню «Категории»: переключить группы.</summary>
    [RelayCommand]
    private void ToggleGroupByCategory() => GroupByCategory = !GroupByCategory;

    /// <summary>Выбрали другой мод — в списках категорий отметить его категорию.</summary>
    partial void OnSelectedChanged(ModRowViewModel? oldValue, ModRowViewModel? newValue)
    {
        if (oldValue?.Category != newValue?.Category) RefreshCategoryLists();
    }

    /// <summary>Пункт меню «Категория»: выбрано несколько модов — все в неё, иначе — выбранный.</summary>
    private void MoveSelected(string category)
    {
        if (IsMulti) SetCategory(SelectedRows.ToList(), category);
        else if (Selected is { } row) SetCategory(row, category);
    }

    /// <summary>
    /// Мод — в категорию (пустая — без категории). Регистр не плодит дублей: «карта» к уже существующей «Карта» — в неё же.
    /// </summary>
    public void SetCategory(ModRowViewModel row, string? category) => SetCategory([row], category);

    /// <summary>
    /// Несколько модов — в одну категорию одним разом: одна запись настроек и одна перестройка таблицы (раньше на каждый
    /// мод — своя запись и своя перестройка, на 150 выбранных модов это было заметно).
    /// </summary>
    public void SetCategory(IReadOnlyList<ModRowViewModel> rows, string? category)
    {
        if (CategoryStore is not { } store) return;
        category = category?.Trim() ?? "";
        if (category.Length > 0 && Categories.FirstOrDefault(c => string.Equals(c, category, StringComparison.CurrentCultureIgnoreCase)) is { } same)
            category = same;
        var changed = rows.Where(r => r.Category != category).ToList();
        if (changed.Count == 0) return;
        foreach (var row in changed)
        {
            var key = CategoryKey(row);
            if (category.Length == 0) store.Remove(key);
            else store[key] = category;
            row.Category = category;
        }
        _main.SaveSettings();
        RefreshCategoryLists();
        RefreshView(); // строки встают в свою группу
    }

    /// <summary>Группа свёрнута (запоминается в профиле, чтобы перестройка таблицы её не раскрывала).</summary>
    public bool IsCategoryCollapsed(string group) =>
        _main.ActiveProfile?.Model.CollapsedModCategories.Contains(group, StringComparer.CurrentCultureIgnoreCase) == true;

    public void SetCategoryCollapsed(string group, bool collapsed)
    {
        if (_main.ActiveProfile?.Model.CollapsedModCategories is not { } list || IsCategoryCollapsed(group) == collapsed) return;
        if (collapsed) list.Add(group);
        else list.RemoveAll(g => string.Equals(g, group, StringComparison.CurrentCultureIgnoreCase));
        _main.SaveSettings();
    }

    private Comparison<ModRowViewModel> CurrentOrder() => ModListRules.Order(SortIndex, GroupByCategory);

    /// <summary>
    /// «Разложить по тегам»: модам без категории — категории по тегам ModDB (<see cref="Core.Mods.ModCategorySuggest"/>).
    /// Теги — из каталога модбазы (кэш на диске, одна загрузка в несколько часов). Что принять — решает окно с галочками.
    /// </summary>
    [RelayCommand]
    private async Task SuggestCategoriesAsync()
    {
        if (CategoryStore is null) return;
        var owner = _main.ActiveProfile;
        var catalog = new Core.ModDb.CatalogService(_db);
        try
        {
            await catalog.LoadAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
                                       or Newtonsoft.Json.JsonException)
        {
            Ui.Warn(Loc.T("catsuggest.noCatalog", ex.Message));
            return;
        }
        // пока грузился каталог, профиль сменили: предложения по модам нового профиля легли бы в категории прежнего
        if (_main.ActiveProfile != owner || CategoryStore is not { } store) return;
        var tags = new Dictionary<string, List<string?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in catalog.Items)
            foreach (var id in item.ModIdStrs.Where(s => !string.IsNullOrWhiteSpace(s)))
                tags.TryAdd(id!, item.Tags);

        var proposals = Rows.Where(r => r.Category.Length == 0 && r.ModId.Length > 0)
            .Select(r => (Row: r, Theme: Core.Mods.ModCategorySuggest.ThemeFor(tags.GetValueOrDefault(r.ModId) ?? [], r.ModId)))
            .Where(x => x.Theme is not null)
            .GroupBy(x => Loc.T(x.Theme!))
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (proposals.Count == 0)
        {
            Ui.Info(Loc.T("catsuggest.nothing"));
            return;
        }
        if (AppWindows.Current.ConfirmCategories([.. proposals.Select(g => new CategoryProposal(g.Key, [.. g.Select(x => x.Row.Name).Order(StringComparer.CurrentCultureIgnoreCase)]))])
            is not { Accepted.Count: > 0 } answer)
            return;

        foreach (var group in proposals.Where(g => answer.Accepted.Contains(g.Key)))
            foreach (var (row, _) in group)
                store[CategoryKey(row)] = group.Key;
        _main.SaveSettings();
        ApplyCategories();
        if (!GroupByCategory) GroupByCategory = true; // разложили — сразу видно по группам
        else RefreshView();
    }
}

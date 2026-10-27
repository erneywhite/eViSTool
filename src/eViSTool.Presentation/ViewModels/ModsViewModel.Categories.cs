using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Presentation.ViewModels.Mods;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Пункт меню «Категория ▸»: перенести выбранный мод в эту категорию («» — без категории).</summary>
public sealed record CategoryChoice(string Label, string Category, IRelayCommand Command);

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
        CategoryMenu.Clear();
        foreach (var name in names) CategoryMenu.Add(new CategoryChoice(name, name, new RelayCommand(() => MoveSelected(name))));
        CategoryMenu.Add(new CategoryChoice(NoCategoryLabel, "", new RelayCommand(() => MoveSelected(""))));
    }

    private void MoveSelected(string category)
    {
        if (Selected is { } row) SetCategory(row, category);
    }

    /// <summary>
    /// Мод — в категорию (пустая — без категории). Регистр не плодит дублей: «карта» к уже существующей «Карта» — в неё же.
    /// </summary>
    public void SetCategory(ModRowViewModel row, string? category)
    {
        if (CategoryStore is not { } store) return;
        category = category?.Trim() ?? "";
        if (category.Length > 0 && Categories.FirstOrDefault(c => string.Equals(c, category, StringComparison.CurrentCultureIgnoreCase)) is { } same)
            category = same;
        if (category == row.Category) return;
        var key = CategoryKey(row);
        if (category.Length == 0) store.Remove(key);
        else store[key] = category;
        _main.SaveSettings();
        row.Category = category;
        RefreshCategoryLists();
        RefreshView(); // строка встаёт в свою группу
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
}

using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Заголовок категории в выпадающем списке ключей настроек мира. У списков окна на Avalonia группировки нет, поэтому
/// список плоский: заголовок, ключи его категории, следующий заголовок… Подпись берётся у первого ключа категории
/// (код категории постоянный, а подпись меняется с языком — так же было в группах окна на WPF).
/// </summary>
public sealed class WorldSettingCategoryHeader : ObservableObject
{
    private readonly WorldSettingKeyOption _first;

    public WorldSettingCategoryHeader(WorldSettingKeyOption first)
    {
        _first = first;
        first.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorldSettingKeyOption.CategoryLabel)) OnPropertyChanged(nameof(Label));
        };
    }

    /// <summary>Код категории.</summary>
    public string Category => _first.Category;

    /// <summary>Название категории на текущем языке.</summary>
    public string Label => _first.CategoryLabel;

    /// <summary>
    /// Текст элемента для редактируемого списка: набранное в поле сравнивается с ним (без учёта регистра, по правилам
    /// языка), и совпадение выбирает элемент. У заголовка этот текст не пустой (очищенное поле иначе выбрало бы первый
    /// заголовок) и начинается с перевода строки: однострочное поле обрезает ввод и вставку на первом переводе строки,
    /// так что набрать такой текст нельзя. Управляющий символ вместо него не годится — сравнение по правилам языка его
    /// пропускает, и «spawnndeath» совпало бы с заголовком.
    /// </summary>
    public override string ToString() => "\n" + Category;
}

/// <summary>Ключи настроек мира с заголовками категорий — плоский список для выпадающего списка ключей.</summary>
public static class WorldSettingKeyList
{
    private static readonly ConditionalWeakTable<IReadOnlyList<WorldSettingKeyOption>, IReadOnlyList<object>> Cache = new();

    /// <summary>
    /// Ключи (уже по порядку категорий) с заголовком перед каждой категорией. На один исходный список — всегда один и
    /// тот же результат: список ключей общий на все строки таблицы, а редактируемый выпадающий список при смене
    /// ItemsSource теряет выбор. Сами ключи — те же объекты: подписи у них обновляются на месте.
    /// </summary>
    public static IReadOnlyList<object> For(IReadOnlyList<WorldSettingKeyOption> options) =>
        Cache.GetValue(options, Build);

    private static IReadOnlyList<object> Build(IReadOnlyList<WorldSettingKeyOption> options)
    {
        var items = new List<object>();
        string? category = null;
        foreach (var option in options)
        {
            if (items.Count == 0 || option.Category != category)
            {
                category = option.Category;
                items.Add(new WorldSettingCategoryHeader(option));
            }
            items.Add(option);
        }
        return items;
    }
}

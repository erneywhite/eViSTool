using System.ComponentModel;
using eViSTool.Core.Localization;
using eViSTool.Core.Server.Config;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Плоский список ключей настроек мира с заголовками категорий — для выпадающего списка ключей в окне на Avalonia
/// (группировки, как у WPF, там нет).
/// </summary>
public sealed class WorldSettingKeyListTests
{
    private static List<WorldSettingKeyOption> AllOptions() => [.. WorldSettingCatalog.All.Select(info => new WorldSettingKeyOption(info))];

    [Fact]
    public void Header_goes_before_each_category_and_keys_keep_their_order()
    {
        var options = AllOptions();
        var items = WorldSettingKeyList.For(options);

        // ключи — те же объекты и в том же порядке
        Assert.Equal(options, items.OfType<WorldSettingKeyOption>());
        // заголовок — по одному на категорию, в порядке каталога
        Assert.Equal(WorldSettingCatalog.Categories, items.OfType<WorldSettingCategoryHeader>().Select(h => h.Category));
        // под заголовком — только ключи его категории
        string? category = null;
        foreach (var item in items)
        {
            if (item is WorldSettingCategoryHeader header) category = header.Category;
            else Assert.Equal(category, ((WorldSettingKeyOption)item).Category);
        }
        Assert.IsType<WorldSettingCategoryHeader>(items[0]);
    }

    [Fact]
    public void Same_keys_give_the_same_list()
    {
        // список ключей общий на все строки таблицы: новый ItemsSource сбросил бы выбор в выпадающем списке
        var options = AllOptions();
        Assert.Same(WorldSettingKeyList.For(options), WorldSettingKeyList.For(options));
        Assert.NotSame(WorldSettingKeyList.For(options), WorldSettingKeyList.For(AllOptions()));
    }

    [Fact]
    public void Empty_keys_give_empty_list() => Assert.Empty(WorldSettingKeyList.For([]));

    [Fact]
    public void Header_label_follows_language()
    {
        var before = Loc.Instance.Language;
        try
        {
            Loc.Instance.SetLanguage("en");
            var options = AllOptions();
            var header = WorldSettingKeyList.For(options).OfType<WorldSettingCategoryHeader>().First();
            var english = header.Label;
            var changed = new List<string?>();
            ((INotifyPropertyChanged)header).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            Loc.Instance.SetLanguage("ru");
            foreach (var option in options) option.RefreshTexts(); // так делает редактор при смене языка

            Assert.Equal("Появление и смерть игрока", header.Label);
            Assert.NotEqual(english, header.Label);
            Assert.Contains(nameof(WorldSettingCategoryHeader.Label), changed);
        }
        finally
        {
            Loc.Instance.SetLanguage(before);
        }
    }

    /// <summary>
    /// Редактируемый список выбирает пункт, чей текст совпал с набранным (без учёта регистра, по правилам языка).
    /// Текст заголовка не совпадает ни с ключом, ни с названием категории, ни с её кодом, ни с пустым полем.
    /// </summary>
    [Fact]
    public void Header_text_matches_nothing_typed()
    {
        var options = AllOptions();
        var headers = WorldSettingKeyList.For(options).OfType<WorldSettingCategoryHeader>().ToList();
        var typed = options.SelectMany(o => new[] { o.Key, o.Label })
            .Concat(headers.SelectMany(h => new[] { h.Label, h.Category }))
            .Append("")
            .ToList();

        // язык системы у пользователя любой: сравнение по правилам языка пропускает управляющие символы
        string[] cultures = ["ru-RU", "en-US", ""];
        foreach (var header in headers)
        {
            var text = header.ToString();
            Assert.False(string.IsNullOrEmpty(text));
            Assert.Contains('\n', text); // однострочное поле обрезает ввод на переводе строки — не набрать
            foreach (var culture in cultures)
            foreach (var input in typed)
                Assert.False(string.Compare(input, text, System.Globalization.CultureInfo.GetCultureInfo(culture), System.Globalization.CompareOptions.IgnoreCase) == 0,
                    $"«{input}» выбрал бы заголовок {header.Category} ({culture})");
        }
    }
}

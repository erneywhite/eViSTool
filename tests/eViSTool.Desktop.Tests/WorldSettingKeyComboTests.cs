using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Core.Server.Config;
using eViSTool.Desktop.Controls;
using eViSTool.Desktop.Converters;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Ключ настройки мира: редактируемый выпадающий список с заголовками категорий (плоский список, как во вкладке
/// «Конфигурация»). Заголовок нельзя выбрать — ни набором текста, ни мышью, ни стрелками.
/// </summary>
public class WorldSettingKeyComboTests
{
    /// <summary>Строка таблицы: общий список ключей и ключ строки.</summary>
    public sealed class Row : ObservableObject
    {
        private string _key = "";

        public IReadOnlyList<WorldSettingKeyOption> KeyOptions { get; init; } = [];

        public string Key
        {
            get => _key;
            set => SetProperty(ref _key, value);
        }
    }

    // первые восемь ключей каталога: вся первая категория и начало второй
    private static readonly IReadOnlyList<WorldSettingKeyOption> Options =
        [.. WorldSettingCatalog.All.Take(8).Select(info => new WorldSettingKeyOption(info))];

    private static List<object> Items => [.. WorldSettingKeyList.For(Options)];

    /// <summary>Как в разметке вкладки: список до текста, контейнер заголовка выключен стилем, стрелки — мимо заголовков.</summary>
    private static (Window Window, ComboBox Combo, Row Row) Show(string key, bool skipHeaders = true)
    {
        var row = new Row { KeyOptions = Options, Key = key };
        var combo = new ComboBox
        {
            IsEditable = true,
            Classes = { "worldKey" },
            [!ItemsControl.ItemsSourceProperty] = new Binding(nameof(Row.KeyOptions)) { Converter = CategoryHeaders.Interleave },
            [!ComboBox.TextProperty] = new Binding(nameof(Row.Key)),
        };
        if (skipHeaders) ComboHeaders.SetHeaderType(combo, typeof(WorldSettingCategoryHeader));
        var window = new Window
        {
            Width = 400,
            Height = 600,
            DataContext = row,
            Content = combo,
            Styles =
            {
                new Style(x => x.OfType<ComboBox>().Class("worldKey").Descendant().OfType<ComboBoxItem>())
                {
                    Setters = { new Setter(InputElement.IsEnabledProperty, new Binding { Converter = CategoryHeaders.IsItem }) },
                },
            },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, combo, row);
    }

    /// <summary>Фокус в поле редактируемого списка, курсор в начале — как после щелчка по полю.</summary>
    private static void FocusField(ComboBox combo)
    {
        combo.Focus();
        Dispatcher.UIThread.RunJobs();
        combo.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "PART_EditableTextBox").CaretIndex = 0;
    }

    private static void Press(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Key_from_model_selects_its_option()
    {
        var key = Options[1].Key;
        var (window, combo, row) = Show(key);

        Assert.Same(Options[1], combo.SelectedItem);
        Assert.Equal(key, combo.Text);
        Assert.Equal(key, row.Key);
        window.Close();
    }

    [AvaloniaFact]
    public void Typed_text_never_selects_a_header()
    {
        var (window, combo, row) = Show(Options[0].Key);
        var header = Items.OfType<WorldSettingCategoryHeader>().First();

        row.Key = header.Label; // название категории
        Assert.Null(combo.SelectedItem);
        row.Key = header.Category; // её код
        Assert.Null(combo.SelectedItem);
        row.Key = ""; // очистили поле
        Assert.Null(combo.SelectedItem);
        Assert.Equal("", combo.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Header_containers_are_disabled()
    {
        var (window, combo, _) = Show("");
        combo.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();

        for (var i = 0; i < Items.Count; i++)
        {
            var container = combo.ContainerFromIndex(i);
            Assert.NotNull(container);
            Assert.Equal(Items[i] is not WorldSettingCategoryHeader, container.IsEnabled);
        }
        combo.IsDropDownOpen = false;
        window.Close();
    }

    /// <summary>Почему нужен ComboHeaders: пока список не открывали, ComboBox сам стрелкой выбирает и заголовок.</summary>
    [AvaloniaFact]
    public void Without_it_arrow_selects_header()
    {
        var items = Items;
        var index = items.IndexOf(items.OfType<WorldSettingCategoryHeader>().Skip(1).First());
        var (window, combo, _) = Show(((WorldSettingKeyOption)items[index + 1]).Key, skipHeaders: false);
        FocusField(combo);

        Press(window, Key.Up, PhysicalKey.ArrowUp);
        Assert.IsType<WorldSettingCategoryHeader>(combo.SelectedItem);
        window.Close();
    }

    [AvaloniaFact]
    public void Arrow_keys_skip_headers()
    {
        // первый ключ второй категории: выше — её заголовок, за ним — последний ключ первой категории
        var items = Items;
        var index = items.IndexOf(items.OfType<WorldSettingCategoryHeader>().Skip(1).First());
        var (window, combo, row) = Show(((WorldSettingKeyOption)items[index + 1]).Key);
        FocusField(combo);

        Press(window, Key.Up, PhysicalKey.ArrowUp);
        Assert.Same(items[index - 1], combo.SelectedItem);
        Assert.Equal(((WorldSettingKeyOption)items[index - 1]).Key, row.Key);

        Press(window, Key.Down, PhysicalKey.ArrowDown);
        Assert.Same(items[index + 1], combo.SelectedItem);
        Assert.Equal(((WorldSettingKeyOption)items[index + 1]).Key, row.Key);

        Press(window, Key.Down, PhysicalKey.ArrowDown); // последний пункт — дальше некуда
        Assert.Same(items[index + 1], combo.SelectedItem);
        window.Close();
    }

    [AvaloniaFact]
    public void Arrow_from_free_text_goes_to_first_key()
    {
        var (window, combo, row) = Show("свой ключ");
        FocusField(combo);

        Press(window, Key.Down, PhysicalKey.ArrowDown);
        Assert.Same(Options[0], combo.SelectedItem); // не заголовок над ним
        Assert.Equal(Options[0].Key, row.Key);
        window.Close();
    }
}

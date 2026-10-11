using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Хватит ли встроенного редактируемого ComboBox (Avalonia 12.1) для полей «список или свой текст» из окна на WPF:
/// ключ настройки мира, цвет роли, адрес агента, чат Telegram.
/// </summary>
public class EditableComboBoxTests
{
    public sealed record Choice(string Name, string Rgb);

    public sealed class Model : ObservableObject
    {
        private string? _text;

        public string? Text
        {
            get => _text;
            set => SetProperty(ref _text, value);
        }
    }

    private static (Window Window, ComboBox Combo, Model Model) Show(object items, Action<ComboBox>? setup = null)
    {
        var model = new Model { Text = "" };
        var combo = new ComboBox
        {
            IsEditable = true,
            ItemsSource = (System.Collections.IEnumerable)items,
            [!ComboBox.TextProperty] = new Binding(nameof(Model.Text)), // по умолчанию двусторонняя
        };
        setup?.Invoke(combo);
        var window = new Window { Width = 300, Height = 300, DataContext = model, Content = combo };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, combo, model);
    }

    [AvaloniaFact]
    public void Typed_free_text_reaches_model_on_every_key()
    {
        var (window, combo, model) = Show(new[] { "192.168.1.10", "10.0.0.5" });
        combo.Focus(); // фокус уходит во внутреннее поле
        Dispatcher.UIThread.RunJobs();

        window.KeyTextInput("my");
        Assert.Equal("my", model.Text); // каждый символ — сразу, как UpdateSourceTrigger=PropertyChanged
        window.KeyTextInput(".host");
        Assert.Equal("my.host", model.Text);
        Assert.Null(combo.SelectedItem);
        window.Close();
    }

    /// <summary>
    /// Начало существующего элемента не выбирает его и не дописывает текст (поиск по первым буквам видит ввод уже
    /// обработанным полем). Автодополнения, как у WPF, тоже нет.
    /// </summary>
    [AvaloniaFact]
    public void Typing_prefix_of_item_keeps_typed_text()
    {
        var (window, combo, model) = Show(new[] { "192.168.1.10", "10.0.0.5" });
        combo.Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyTextInput("10.0");
        Assert.Equal("10.0", model.Text);
        Assert.Null(combo.SelectedItem);

        window.KeyTextInput(".0.5"); // набрали целиком — элемент выбран, текст тот же
        Assert.Equal("10.0.0.5", combo.SelectedItem);
        Assert.Equal("10.0.0.5", model.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Picking_from_list_writes_text()
    {
        var (window, combo, model) = Show(new[] { "192.168.1.10", "10.0.0.5" });
        combo.SelectedIndex = 1;
        Assert.Equal("10.0.0.5", model.Text);
        Assert.Equal("10.0.0.5", combo.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Model_text_selects_matching_item()
    {
        var (window, combo, model) = Show(new[] { "192.168.1.10", "10.0.0.5" });
        model.Text = "10.0.0.5";
        Assert.Equal("10.0.0.5", combo.SelectedItem);
        model.Text = "elsewhere";
        Assert.Null(combo.SelectedItem);
        Assert.Equal("elsewhere", combo.Text);
        window.Close();
    }

    /// <summary>Объекты в списке: текст поля — их свойство (в WPF — TextSearch.TextPath, здесь TextSearch.TextBinding).</summary>
    [AvaloniaFact]
    public void Object_items_use_text_binding()
    {
        var colors = new[] { new Choice("Красный", "#FF0000"), new Choice("Лесной", "#2E4A2E") };
        var (window, combo, model) = Show(colors, c => TextSearch.SetTextBinding(c, new Binding(nameof(Choice.Name))));

        combo.SelectedIndex = 1;
        Assert.Equal("Лесной", model.Text);

        combo.Focus();
        Dispatcher.UIThread.RunJobs();
        var box = combo.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "PART_EditableTextBox");
        box.SelectAll();
        window.KeyTextInput("#123456");
        Assert.Equal("#123456", model.Text);
        Assert.Null(combo.SelectedItem);

        model.Text = "красный"; // совпадение без учёта регистра выбирает элемент, текст не трогает
        Assert.Same(colors[0], combo.SelectedItem);
        Assert.Equal("красный", combo.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Drop_down_opens_from_code()
    {
        var (window, combo, _) = Show(new[] { "чат 1", "чат 2" });
        combo.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();

        var popup = combo.GetVisualDescendants().OfType<Popup>().First();
        Assert.True(popup.IsOpen);
        Assert.NotNull(combo.ContainerFromIndex(1)); // строки списка созданы

        combo.IsDropDownOpen = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(popup.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void New_items_source_keeps_typed_text()
    {
        // чат Telegram: «Найти» подменяет список, а уже вписанный id не должен пропасть
        var (window, combo, model) = Show(Array.Empty<string>());
        model.Text = "-100123";
        combo.ItemsSource = new[] { "Мой чат", "Группа" };
        Assert.Equal("-100123", combo.Text);
        Assert.Equal("-100123", model.Text);
        window.Close();
    }
}

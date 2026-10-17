using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using eViSTool.Desktop.Controls;

namespace eViSTool.Desktop.Tests;

public class DoubleTapTests
{
    private static (Window Window, ListBox List, RecordingCommand Command) ShowList(int count)
    {
        var command = new RecordingCommand();
        var list = new ListBox { ItemsSource = Enumerable.Range(1, count).Select(i => $"item {i}").ToList() };
        DoubleTap.SetCommand(list, command);
        var window = new Window { Width = 300, Height = 300, Content = list };
        window.Show();
        return (window, list, command);
    }

    [AvaloniaFact]
    public void Row_executes_command_with_its_item()
    {
        var (window, list, command) = ShowList(3);
        Mouse.Click(window, list.ContainerFromIndex(1)!, count: 2);

        Assert.Equal(["item 2"], command.Calls);
        window.Close();
    }

    [AvaloniaFact]
    public void Single_click_does_nothing()
    {
        var (window, list, command) = ShowList(3);
        Mouse.Click(window, list.ContainerFromIndex(0)!);

        Assert.Empty(command.Calls);
        window.Close();
    }

    [AvaloniaFact]
    public void Empty_space_below_rows_does_nothing()
    {
        var (window, _, command) = ShowList(2);
        Mouse.ClickAt(window, new Point(150, 280), count: 2);

        Assert.Empty(command.Calls);
        window.Close();
    }

    [AvaloniaFact]
    public void Scroll_bar_does_nothing()
    {
        var (window, list, command) = ShowList(100);
        var bar = list.GetVisualDescendants().OfType<ScrollBar>().First(b => b.Orientation == Orientation.Vertical && b.IsVisible);
        Mouse.Click(window, bar, count: 2);

        Assert.Empty(command.Calls);
        window.Close();
    }

    [AvaloniaFact]
    public void Explicit_parameter_wins()
    {
        var (window, list, command) = ShowList(3);
        DoubleTap.SetCommandParameter(list, "fixed");
        Mouse.Click(window, list.ContainerFromIndex(0)!, count: 2);

        Assert.Equal(["fixed"], command.Calls);
        window.Close();
    }

    [AvaloniaFact]
    public void Button_inside_row_does_nothing()
    {
        var command = new RecordingCommand();
        var list = new ItemsControl
        {
            ItemsSource = new[] { "a" },
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((_, _) =>
                // фон строки — чтобы щелчок мимо букв попадал в строку, как у ListBoxItem
                new DockPanel
                {
                    Background = Avalonia.Media.Brushes.Transparent,
                    Children = { new Button { Content = "x", [DockPanel.DockProperty] = Dock.Left }, new TextBlock { Text = "row" } },
                }),
        };
        DoubleTap.SetCommand(list, command);
        var window = new Window { Width = 300, Height = 300, Content = list };
        window.Show();

        var row = list.ContainerFromIndex(0)!;
        Mouse.Click(window, row.GetVisualDescendants().OfType<Button>().First(), count: 2);
        Assert.Empty(command.Calls);

        Mouse.Click(window, row.GetVisualDescendants().OfType<TextBlock>().Last(), count: 2);
        Assert.Equal(["a"], command.Calls);
        window.Close();
    }

    public sealed record Mod(string Name);

    [AvaloniaFact]
    public void DataGrid_row_yes_header_no()
    {
        var command = new RecordingCommand();
        var mods = new[] { new Mod("alpha"), new Mod("beta") };
        var grid = new DataGrid
        {
            ItemsSource = mods,
            IsReadOnly = true,
            Columns = { new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Mod.Name)), Width = new DataGridLength(200) } },
        };
        DoubleTap.SetCommand(grid, command);
        var window = new Window { Width = 400, Height = 300, Content = grid };
        window.Show();

        var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().First(h => h.Bounds.Width > 0);
        Mouse.Click(window, header, count: 2);
        Assert.Empty(command.Calls);

        var row = grid.GetVisualDescendants().OfType<DataGridRow>().First(r => ReferenceEquals(r.DataContext, mods[1]));
        Mouse.Click(window, row, count: 2);
        Assert.Equal([mods[1]], command.Calls);

        Mouse.ClickAt(window, new Point(200, 280), count: 2); // пустое место под строками
        Assert.Single(command.Calls);
        window.Close();
    }
}

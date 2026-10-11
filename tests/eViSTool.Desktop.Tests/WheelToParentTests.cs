using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eViSTool.Desktop.Controls;

namespace eViSTool.Desktop.Tests;

public class WheelToParentTests
{
    private static (Window Window, ScrollViewer Scroll, ComboBox Combo) Build(bool wheelToParent)
    {
        var combo = new ComboBox { ItemsSource = new[] { "один", "два", "три" }, SelectedIndex = 0, Width = 200 };
        WheelToParent.SetIsEnabled(combo, wheelToParent);
        var scroll = new ScrollViewer
        {
            Content = new StackPanel
            {
                Children = { new Border { Height = 100 }, combo, new Border { Height = 1000 } },
            },
        };
        var window = new Window { Width = 300, Height = 300, Content = scroll };
        window.Show();
        combo.Focus(); // в фокусе ComboBox крутит выбор колесом — то, от чего защищаемся
        Dispatcher.UIThread.RunJobs();
        return (window, scroll, combo);
    }

    private static Point Center(Visual visual, Window window) =>
        visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), window)!.Value;

    [AvaloniaFact]
    public void Without_it_focused_combo_eats_wheel()
    {
        var (window, scroll, combo) = Build(wheelToParent: false);
        window.MouseWheel(Center(combo, window), new Vector(0, -1));

        Assert.Equal(1, combo.SelectedIndex);
        Assert.Equal(0, scroll.Offset.Y);
        window.Close();
    }

    [AvaloniaFact]
    public void Wheel_scrolls_page_and_keeps_value()
    {
        var (window, scroll, combo) = Build(wheelToParent: true);
        window.MouseWheel(Center(combo, window), new Vector(0, -1));

        Assert.Equal(0, combo.SelectedIndex);
        Assert.True(scroll.Offset.Y > 0);

        var offset = scroll.Offset.Y;
        window.MouseWheel(Center(combo, window), new Vector(0, 1)); // обратно вверх — тоже страница
        Assert.True(scroll.Offset.Y < offset);
        Assert.Equal(0, combo.SelectedIndex);
        window.Close();
    }

    [AvaloniaFact]
    public void Open_drop_down_is_left_alone()
    {
        var (window, scroll, combo) = Build(wheelToParent: true);
        combo.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
        window.MouseWheel(Center(combo, window), new Vector(0, -1));

        Assert.Equal(0, scroll.Offset.Y);
        window.Close();
    }
}

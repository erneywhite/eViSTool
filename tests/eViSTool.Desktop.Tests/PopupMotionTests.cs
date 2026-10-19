using System.Diagnostics;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Desktop.Controls;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Выпадающие меню и списки (docs/design.md, раздел 3): при каждом открытии содержимое Popup проявляется — прозрачность
/// с нуля и сдвиг на 4 px сверху (раскрывшееся вверх — снизу), 160 мс — и в конце стоит на месте без своих значений;
/// закрытие обрывает проявление сразу; при системном «без анимаций» меню сразу целиком. У каких Popup так — решает
/// Themes/ForestPopups.axaml, его стенд и подключает.
/// </summary>
public sealed class PopupMotionTests
{
    private static readonly Uri Host = new("avares://eViSTool.Desktop.Tests");

    private static double Shift(Visual visual) => visual.RenderTransform?.Value.M32 ?? 0;

    private static void Tick()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    // стили — до содержимого, как у программы (App.axaml): Popup, уже лежавший в окне, стиль, подключённый к окну позже,
    // в Avalonia 12.1.4 не получил
    private static Window PopupWindow(Control content, bool still = false)
    {
        var window = new Window { Width = 500, Height = 400 };
        window.Styles.Add(new StyleInclude(Host) { Source = new Uri("avares://eViSTool/Themes/ForestPopups.axaml") });
        window.Content = content;
        if (still) Motion.SetEnabled(window, false);
        window.Show();
        Tick();
        return window;
    }

    /// <summary>Что было с прозрачностью и сдвигом элемента, пока за ним смотрели.</summary>
    private sealed class Probe : IDisposable
    {
        private readonly Visual _target;

        public Probe(Visual target)
        {
            _target = target;
            MinOpacity = target.Opacity;
            MinShift = MaxShift = Shift(target);
            target.PropertyChanged += OnChanged;
        }

        public double MinOpacity { get; private set; }
        public double MinShift { get; private set; }
        public double MaxShift { get; private set; }

        private void OnChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.OpacityProperty) MinOpacity = Math.Min(MinOpacity, _target.Opacity);
            else if (e.Property == Visual.RenderTransformProperty)
            {
                MinShift = Math.Min(MinShift, Shift(_target));
                MaxShift = Math.Max(MaxShift, Shift(_target));
            }
        }

        /// <summary>Выпало: начало почти с прозрачного и выше места, не выше чем на 4 px, вниз не уходило.</summary>
        public void AssertDroppedFromAbove()
        {
            Assert.InRange(MinOpacity, 0, 0.25);
            Assert.InRange(MinShift, -PopupMotion.Shift - 0.01, -PopupMotion.Shift * 0.7);
            Assert.Equal(0, MaxShift);
        }

        /// <summary>Поднялось: начало почти с прозрачного и ниже места, не ниже чем на 4 px.</summary>
        public void AssertRoseFromBelow()
        {
            Assert.InRange(MinOpacity, 0, 0.25);
            Assert.InRange(MaxShift, PopupMotion.Shift * 0.7, PopupMotion.Shift + 0.01);
            Assert.Equal(0, MinShift);
        }

        /// <summary>Показалось сразу целиком, без движения.</summary>
        public void AssertStill()
        {
            Assert.Equal(1, MinOpacity);
            Assert.Equal(0, MinShift);
            Assert.Equal(0, MaxShift);
        }

        public void Dispose() => _target.PropertyChanged -= OnChanged;
    }

    /// <summary>Пришло на место: прозрачность 1, сдвига нет, и своих значений проявление не оставило.</summary>
    private static void AssertInPlace(Visual visual)
    {
        Assert.Equal(1, visual.Opacity);
        Assert.Equal(0, Shift(visual));
        Assert.False(visual.IsSet(Visual.OpacityProperty));
        Assert.False(visual.IsSet(Visual.RenderTransformProperty));
    }

    [AvaloniaFact]
    public async Task Combo_dropdown_drops_in_on_every_opening_and_ends_in_place()
    {
        var combo = new ComboBox { ItemsSource = new[] { "Стенд", "Сервер", "Клиент" }, Width = 160, SelectedIndex = 0 };
        var window = PopupWindow(new StackPanel { Margin = new Thickness(20), Children = { combo } });
        var popup = combo.GetVisualDescendants().OfType<Popup>().Single();
        Assert.True(PopupMotion.GetReveal(popup)); // стиль из ForestPopups.axaml
        var list = Assert.IsAssignableFrom<Control>(popup.Child);

        // не только при первом открытии: стиль с анимацией сработал бы один раз, а проявление — на каждое
        for (var round = 0; round < 2; round++)
        {
            using var seen = new Probe(list);
            var watch = Stopwatch.StartNew();
            combo.IsDropDownOpen = true;
            Tick();
            Assert.True(popup.IsOpen);
            if (watch.ElapsedMilliseconds < 80) Assert.InRange(list.Opacity, 0, 0.99); // на первом кадре ещё идёт

            await Wait.Frames(300);
            seen.AssertDroppedFromAbove();
            AssertInPlace(list);
            combo.IsDropDownOpen = false;
            Tick();
        }
        window.Close();
    }

    private sealed class ProbedMenuFlyout : MenuFlyout
    {
        public Control? Presenter { get; private set; }
        protected override Control CreatePresenter() => Presenter = base.CreatePresenter();
    }

    [AvaloniaFact]
    public async Task Menu_flyout_and_context_menu_drop_in_too()
    {
        var flyout = new ProbedMenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft, Items = { new MenuItem { Header = "Экспорт" } } };
        var button = new Button { Content = "Модпак", Flyout = flyout };
        var menu = new ContextMenu { Items = { new MenuItem { Header = "Копировать" } } };
        var console = new Border { Height = 200, Background = Brushes.Gray, ContextMenu = menu };
        var window = PopupWindow(new StackPanel { Margin = new Thickness(20), Spacing = 10, Children = { button, console } });

        flyout.ShowAt(button);
        var presenter = Assert.IsAssignableFrom<Control>(flyout.Presenter);
        using (var seen = new Probe(presenter))
        {
            Tick();
            await Wait.Frames(300);
            seen.AssertDroppedFromAbove();
        }
        AssertInPlace(presenter);
        flyout.Hide();
        Tick();

        // контекстное меню привязано к точке нажатия, которой окно не знает: всегда сверху вниз
        using (var seen = new Probe(menu))
        {
            menu.Open(console);
            Tick();
            await Wait.Frames(300);
            seen.AssertDroppedFromAbove();
        }
        AssertInPlace(menu);
        menu.Close();
        window.Close();
    }

    /// <summary>Свой Popup над полем (подсказки команд консоли): раскрывается вверх — и поднимается снизу.</summary>
    [AvaloniaFact]
    public async Task Popup_above_its_field_rises_from_below()
    {
        var field = new TextBox { Width = 200, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(20) };
        var hints = new Border { Width = 200, Height = 80, Background = Brushes.Gray };
        var popup = new Popup { PlacementTarget = field, Placement = PlacementMode.TopEdgeAlignedLeft, Child = hints };
        var window = PopupWindow(new Panel { Children = { field, popup } });

        using (var seen = new Probe(hints))
        {
            popup.IsOpen = true;
            Tick();
            await Wait.Frames(300);
            seen.AssertRoseFromBelow();
        }
        AssertInPlace(hints);
        // и по месту на экране (меню уже поставлено): середина выше середины поля
        Assert.True(PopupMotion.OpensUp(popup, hints));

        // тот же Popup под полем — уже сверху вниз
        popup.IsOpen = false;
        popup.Placement = PlacementMode.BottomEdgeAlignedLeft;
        field.VerticalAlignment = VerticalAlignment.Top;
        Tick();
        using (var seen = new Probe(hints))
        {
            popup.IsOpen = true;
            Tick();
            await Wait.Frames(300);
            seen.AssertDroppedFromAbove();
        }
        Assert.False(PopupMotion.OpensUp(popup, hints));
        popup.IsOpen = false;
        window.Close();
    }

    /// <summary>
    /// Список у нижнего края экрана не помещается вниз и раскрывается над полем: направление берётся с места на экране.
    /// Своё окно меню (Windows, Linux) к открытию уже стоит там; у стенда меню в слое окна, и место ему дают к кадру —
    /// поэтому смотрим после кадров.
    /// </summary>
    [AvaloniaFact]
    public async Task Combo_flipped_above_at_the_screen_edge_counts_as_opening_up()
    {
        var combo = new ComboBox
        {
            ItemsSource = Enumerable.Range(1, 8).Select(i => $"Профиль {i}").ToArray(), Width = 160,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(20, 0, 0, 4),
        };
        var window = PopupWindow(new Panel { Children = { combo } });
        var screen = window.Screens?.Primary;
        Assert.NotNull(screen);
        window.Height = screen.Bounds.Height; // окно во весь экран: поле у нижнего края
        Tick();
        var popup = combo.GetVisualDescendants().OfType<Popup>().Single();
        var list = Assert.IsAssignableFrom<Control>(popup.Child);

        combo.IsDropDownOpen = true;
        Tick();
        await Wait.Frames(300);
        Assert.True(list.PointToScreen(default).Y < combo.PointToScreen(default).Y, "список не перевернулся над полем");
        Assert.True(PopupMotion.OpensUp(popup, list));
        AssertInPlace(list);
        combo.IsDropDownOpen = false;
        window.Close();
    }

    [AvaloniaFact]
    public async Task Closing_mid_reveal_drops_it_at_once()
    {
        var combo = new ComboBox { ItemsSource = new[] { "ru", "en" }, Width = 120 };
        var window = PopupWindow(new StackPanel { Margin = new Thickness(20), Children = { combo } });
        var list = Assert.IsAssignableFrom<Control>(combo.GetVisualDescendants().OfType<Popup>().Single().Child);

        combo.IsDropDownOpen = true;
        Tick();
        combo.IsDropDownOpen = false; // закрыли, не дождавшись: проявление оборвано, своих значений нет
        Tick();
        Assert.False(list.IsSet(Visual.OpacityProperty));
        Assert.False(list.IsSet(Visual.RenderTransformProperty));

        // и следующее открытие проявляется заново, как обычно
        using var seen = new Probe(list);
        combo.IsDropDownOpen = true;
        Tick();
        await Wait.Frames(300);
        seen.AssertDroppedFromAbove();
        AssertInPlace(list);
        combo.IsDropDownOpen = false;
        window.Close();
    }

    [AvaloniaFact]
    public async Task Without_motion_menu_opens_at_once()
    {
        var combo = new ComboBox { ItemsSource = new[] { "ru", "en" }, Width = 120 };
        var window = PopupWindow(new StackPanel { Margin = new Thickness(20), Children = { combo } }, still: true);
        var list = Assert.IsAssignableFrom<Control>(combo.GetVisualDescendants().OfType<Popup>().Single().Child);

        using var seen = new Probe(list);
        combo.IsDropDownOpen = true;
        Tick();
        await Wait.Frames(250);
        seen.AssertStill();
        Assert.True(combo.IsDropDownOpen);
        combo.IsDropDownOpen = false;
        window.Close();
    }

    [AvaloniaFact]
    public async Task Popup_without_reveal_is_untouched()
    {
        var hints = new Border { Width = 100, Height = 40, Background = Brushes.Gray };
        var popup = new Popup { Child = hints, Placement = PlacementMode.Bottom };
        var window = PopupWindow(new StackPanel { Margin = new Thickness(20), Children = { new TextBlock { Text = "поле" }, popup } });
        PopupMotion.SetReveal(popup, false); // свой локальный False сильнее стиля темы

        using var seen = new Probe(hints);
        popup.IsOpen = true;
        Tick();
        await Wait.Frames(250);
        seen.AssertStill();
        popup.IsOpen = false;
        window.Close();
    }

    /// <summary>Стили выпадающих подключены там же, где остальная тема окна, — в App.axaml.</summary>
    [Fact]
    public void Popup_styles_are_part_of_the_app_theme()
    {
        var app = XDocument.Load(System.IO.Path.Combine(ThemeTests.RepoRoot(), "src", "eViSTool.Desktop", "App.axaml"));
        var includes = app.Descendants().Where(e => e.Name.LocalName == "StyleInclude").Select(e => (string?)e.Attribute("Source")).ToList();
        Assert.Contains("avares://eViSTool/Themes/ForestPopups.axaml", includes);
    }
}

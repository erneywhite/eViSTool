using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Server;
using eViSTool.Desktop.Platform;
using eViSTool.Desktop.Views;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Вкладка «Объявления» с настоящей разметкой и лесной темой: Enter в поле «Сказать», красная рамка у непонятного
/// интервала, переключение «каждые»/«в», удаление строки, блок приветствия по состоянию конфига.
/// Лесная тема подключается к приложению тестов на время теста: вкладка берёт из неё иконки, переключатель и кнопки.
/// </summary>
public sealed class ServerAnnouncementsTabTests : IDisposable
{
    private readonly ResourceInclude _resources = new(new Uri("avares://eViSTool.Desktop.Tests"))
    {
        Source = new Uri("avares://eViSTool.Desktop/Themes/ForestResources.axaml"),
    };

    private readonly ResourceInclude _controls = new(new Uri("avares://eViSTool.Desktop.Tests"))
    {
        Source = new Uri("avares://eViSTool.Desktop/Themes/ForestControls.axaml"),
    };

    private readonly StyleInclude _styles = new(new Uri("avares://eViSTool.Desktop.Tests"))
    {
        Source = new Uri("avares://eViSTool.Desktop/Themes/Forest.axaml"),
    };

    private readonly IUiPlatform? _oldPlatform = Ui.IsSet ? Ui.Platform : null;

    public ServerAnnouncementsTabTests()
    {
        Ui.Platform = new AvaloniaUiPlatform(); // конфигу сервера в конструкторе нужен таймер платформы
        var app = Application.Current!;
        app.Resources.MergedDictionaries.Add(_resources);
        app.Resources.MergedDictionaries.Add(_controls);
        app.Styles.Add(_styles);
    }

    public void Dispose()
    {
        var app = Application.Current!;
        app.Styles.Remove(_styles);
        app.Resources.MergedDictionaries.Remove(_controls);
        app.Resources.MergedDictionaries.Remove(_resources);
        if (_oldPlatform is not null) Ui.Platform = _oldPlatform;
    }

    private static (Window Window, ServerAnnouncementsTab Tab, ServerAnnouncementsViewModel Model) Show(params Announcement[] items)
    {
        var model = new ServerViewModel(null!).Announcements;
        foreach (var a in items) model.Items.Add(new AnnouncementRowViewModel(model, a));
        var tab = new ServerAnnouncementsTab { DataContext = model };
        var window = new Window { Width = 1000, Height = 900, Content = tab };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, tab, model);
    }

    private static IEnumerable<TextBox> Boxes(Visual root) => root.GetVisualDescendants().OfType<TextBox>();

    [AvaloniaFact]
    public void Enter_in_say_box_runs_the_bound_command()
    {
        var (window, tab, _) = Show();
        var box = tab.FindControl<TextBox>("SayBox")!;
        var command = new RecordingCommand();
        ((KeyBinding)box.KeyBindings[0]).Command = command; // настоящая привязка команды «Сказать» ждёт запущенного сервера
        box.IsEnabled = true; // у поля — местное значение вместо привязки к CanSay
        box.Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Single(command.Calls);
        window.Close();
    }

    [AvaloniaFact]
    public void Say_box_and_button_are_off_while_the_server_is_not_running()
    {
        var (window, tab, model) = Show();
        Assert.False(model.CanSay);
        Assert.False(tab.FindControl<TextBox>("SayBox")!.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void Bad_interval_keeps_its_red_border_when_focused_and_hovered()
    {
        var (window, tab, model) = Show(new Announcement("Правила в Discord", 30));
        var row = model.Items[0];
        var interval = Boxes(tab).First(b => b.Width == 96);
        var danger = (SolidColorBrush)Application.Current!.FindResource("Forest.Danger")!;
        IBrush? Border() => interval.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement").BorderBrush;

        Assert.DoesNotContain("bad", interval.Classes);
        Assert.NotEqual(danger.Color, (Border() as ISolidColorBrush)?.Color);

        row.IntervalBad = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("bad", interval.Classes);
        Assert.Equal(danger.Color, ((ISolidColorBrush)Border()!).Color);

        var point = interval.TranslatePoint(new Point(interval.Bounds.Width / 2, interval.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); // наведение: у поля в шаблоне своя рамка для :pointerover
        Dispatcher.UIThread.RunJobs();
        Assert.True(interval.IsPointerOver);
        Assert.Equal(danger.Color, ((ISolidColorBrush)Border()!).Color);

        interval.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(interval.IsFocused);
        Assert.Equal(danger.Color, ((ISolidColorBrush)Border()!).Color);

        row.IntervalBad = false;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain("bad", interval.Classes);
        Assert.NotEqual(danger.Color, ((ISolidColorBrush)Border()!).Color);
        window.Close();
    }

    [AvaloniaFact]
    public void Mode_box_switches_between_interval_and_times()
    {
        var (window, tab, model) = Show(new Announcement("Рестарт", 30));
        var row = model.Items[0];
        var interval = Boxes(tab).First(b => b.Width == 96);
        var times = Boxes(tab).First(b => b.Width == 150);
        var mode = tab.GetVisualDescendants().OfType<ComboBox>().First();
        Assert.True(interval.IsEffectivelyVisible);
        Assert.False(times.IsEffectivelyVisible);

        mode.SelectedIndex = 1; // «в»
        Dispatcher.UIThread.RunJobs();
        Assert.True(row.IsTimed);
        Assert.Equal("12:00", row.TimesText); // пустое поле времени модель заполняет сама
        Assert.False(interval.IsEffectivelyVisible);
        Assert.True(times.IsEffectivelyVisible);
        Assert.Equal("12:00", times.Text);

        row.IsTimed = false; // модель переключила режим сама — список «каждые» следует за ней
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, mode.SelectedIndex);
        Assert.True(interval.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Delete_button_removes_its_own_row()
    {
        var (window, tab, model) = Show(new Announcement("первое", 10), new Announcement("второе", 20), new Announcement("третье", 30));
        var buttons = tab.GetVisualDescendants().OfType<Button>().Where(b => b.CommandParameter is AnnouncementRowViewModel).ToList();
        Assert.Equal(3, buttons.Count);
        var second = model.Items[1];
        Assert.Same(second, buttons[1].CommandParameter);

        buttons[1].Command!.Execute(buttons[1].CommandParameter);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["первое", "третье"], model.Items.Select(r => r.Text));
        window.Close();
    }

    [AvaloniaFact]
    public void Welcome_block_follows_the_config_state()
    {
        var (window, tab, model) = Show();
        var texts = () => tab.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.DoesNotContain(eViSTool.Core.Localization.Loc.T("ann.welcomeNoConfig"), texts());

        model.Config.State = ConfigLoadState.Missing;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(eViSTool.Core.Localization.Loc.T("ann.welcomeNoConfig"), texts());

        model.Config.State = ConfigLoadState.Loading;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(eViSTool.Core.Localization.Loc.T("srvcfg.loadingRemote"), texts());
        Assert.DoesNotContain(eViSTool.Core.Localization.Loc.T("ann.welcomeNoConfig"), texts());

        model.Config.State = ConfigLoadState.Loaded; // конфиг без поля приветствия: «не задано»
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(eViSTool.Core.Localization.Loc.T("srvcfg.loadingRemote"), texts());
        Assert.Contains(eViSTool.Core.Localization.Loc.T("ann.welcomeEmpty"), texts());
        window.Close();
    }
}

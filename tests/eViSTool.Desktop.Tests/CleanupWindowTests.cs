using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Profiles;
using eViSTool.Desktop.Dialogs;
using eViSTool.Desktop.Platform;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;
using GlyphPath = Avalonia.Controls.Shapes.Path;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Окно «Уборка диска» с настоящей разметкой и лесной темой: открылось — модель ищет мусор и следит за игрой, закрылось —
/// перестаёт; пометки «безопасно» / «осторожно» своими цветами (в окне на WPF — DataTrigger); у папки целиком нет
/// списка пунктов; галочка «частично» — черта; запущенный сервер — предупреждение и выключенная кнопка; Escape и
/// «Закрыть». Диск и процессы подменены: поиск отдаёт готовые группы, ничего не удаляется.
/// </summary>
public sealed class CleanupWindowTests : IDisposable
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
    private readonly TimerSpy _spy;
    private bool _running;
    private int _scans;

    public CleanupWindowTests()
    {
        _spy = TimerSpy.Wrap(new AvaloniaUiPlatform());
        Ui.Platform = (IUiPlatform)(object)_spy;
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

    private static CleanupItem Item(CleanupKind kind, string title, bool dflt) =>
        new(kind, title, [title], 1 << 20, new DateTime(2026, 10, 1), dflt);

    /// <summary>Логи (папка целиком, безопасно), моды серверов (часть отмечена), карты (осторожно).</summary>
    private IReadOnlyList<CleanupGroup> Scan(GameProfile profile, string dataDir)
    {
        Interlocked.Increment(ref _scans);
        return
        [
            new CleanupGroup(CleanupKind.OldLogs, CleanupSafety.Safe, [Item(CleanupKind.OldLogs, "Logs\\Archive", true)]),
            new CleanupGroup(CleanupKind.ServerMods, CleanupSafety.Safe,
                [Item(CleanupKind.ServerMods, "178.172.138.142:42420", true), Item(CleanupKind.ServerMods, "95.216.40.12:42420", false)]),
            new CleanupGroup(CleanupKind.WorldMaps, CleanupSafety.Careful, [Item(CleanupKind.WorldMaps, "Дом", false)]),
        ];
    }

    private (CleanupWindow Window, CleanupViewModel Model) Open()
    {
        var profile = new GameProfile { Id = "clean-win", Name = "Стенд", Kind = ProfileKind.Server, DataDir = Path.GetTempPath() };
        var model = new CleanupViewModel(profile, new CleanupServices
        {
            IsRunning = _ => _running,
            Scan = Scan,
            Trash = _ => throw new Xunit.Sdk.XunitException("в тесте окна ничего не убирается"),
        });
        var window = new CleanupWindow { DataContext = model, RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, model);
    }

    private static T Named<T>(Window window, string automationId) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    private static Color Resource(string key) => ((ISolidColorBrush)Application.Current!.FindResource(ThemeVariant.Dark, key)!).Color;

    private static Color Brush(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    [AvaloniaFact]
    public async Task Opening_scans_and_watches_the_game_closing_stops_watching()
    {
        var (window, model) = Open();
        await Wait.Until(() => model.Groups.Count == 3);

        Assert.Equal(1, _scans);
        Assert.Equal(model.Heading, window.Title);
        var timer = Assert.Single(_spy.Timers);
        Assert.Equal(TimeSpan.FromSeconds(2), timer.Interval);
        Assert.True(timer.IsEnabled);

        window.Close();
        Assert.False(timer.IsEnabled);
        Assert.Equal(1, _scans);
    }

    [AvaloniaFact]
    public async Task Safety_badges_item_lists_and_partial_checkbox_look_like_wpf()
    {
        var (window, model) = Open();
        try
        {
            await Wait.Until(() => model.Groups.Count == 3);
            Dispatcher.UIThread.RunJobs();

            var badges = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("safety")).ToList();
            Assert.Equal(3, badges.Count);
            Assert.Equal(Resource("Forest.Selected"), Brush(badges[0].Background));
            Assert.Equal(Resource("Forest.Good"), Brush(((TextBlock)badges[0].Child!).Foreground));
            Assert.Equal(Resource("Forest.Raised"), Brush(badges[2].Background));
            Assert.Equal(Resource("Forest.Warn"), Brush(((TextBlock)badges[2].Child!).Foreground));
            Assert.Equal(model.Groups[2].SafetyText, ((TextBlock)badges[2].Child!).Text);

            // у логов (папка целиком) списка пунктов нет, у модов серверов — есть; галочки пунктов — по умолчанию модели
            var checks = window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible).ToList();
            Assert.Equal([model.Groups[0].Title, model.Groups[1].Title, "178.172.138.142:42420", "95.216.40.12:42420", model.Groups[2].Title, "Дом"],
                checks.Select(c => AutomationProperties.GetName(c)));
            Assert.Equal([true, null, true, false, false, false], checks.Select(c => c.IsChecked));

            // часть пунктов отмечена — черта (как у Fluent в окне на WPF), а не квадрат
            var glyph = checks[1].GetVisualDescendants().OfType<GlyphPath>().Single(p => p.Name == "CheckGlyph");
            Assert.True(glyph.Bounds.Width > 4 * glyph.Bounds.Height, $"значок {glyph.Bounds.Width}×{glyph.Bounds.Height}");

            // галочка группы отмечает все пункты, и сумма на кнопке пересчитывается
            checks[1].IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.All(model.Groups[1].Items, i => Assert.True(i.IsChecked));
            Assert.Equal(model.RemoveText, Named<Button>(window, "CleanRemove").Content);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Running_server_shows_warning_and_disables_remove_status_and_bin_follow_model()
    {
        var (window, model) = Open();
        try
        {
            await Wait.Until(() => model.Groups.Count == 3);
            Dispatcher.UIThread.RunJobs();
            var remove = Named<Button>(window, "CleanRemove");
            var warning = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == model.RunningText);
            Assert.False(warning.IsVisible);
            Assert.True(remove.IsEffectivelyEnabled);

            _running = true;
            model.CheckRunning(); // то же, что сделает таймер через 2 секунды
            Dispatcher.UIThread.RunJobs();
            Assert.True(warning.IsVisible);
            Assert.False(remove.IsEffectivelyEnabled);

            // строка состояния — только с текстом; «Открыть корзину» — когда что-то уже убрано
            var bin = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, model.OpenRecycleBinCommand));
            Assert.False(bin.IsVisible);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Готово");
            model.Status = "Готово";
            model.HasRecycled = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(bin.IsVisible);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Готово");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Nothing_to_clean_says_so()
    {
        var profile = new GameProfile { Id = "clean-empty", Name = "Пусто", Kind = ProfileKind.Client, DataDir = Path.GetTempPath() };
        var model = new CleanupViewModel(profile, new CleanupServices { IsRunning = _ => false, Scan = (_, _) => [] });
        var window = new CleanupWindow { DataContext = model };
        window.Show();
        try
        {
            await Wait.Until(() => !model.IsBusy && model.Status == "");
            Dispatcher.UIThread.RunJobs();
            var nothing = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == eViSTool.Core.Localization.Loc.T("clean.nothing"));
            Assert.True(nothing.IsEffectivelyVisible);
            Assert.False(Named<Button>(window, "CleanRemove").IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Escape_and_close_button_close_the_window()
    {
        var (window, model) = Open();
        await Wait.Until(() => model.Groups.Count == 3);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(window.IsVisible);
        Assert.False(Assert.Single(_spy.Timers).IsEnabled);

        var (second, secondModel) = Open();
        await Wait.Until(() => secondModel.Groups.Count == 3);
        var close = second.GetVisualDescendants().OfType<Button>().Single(b => b.IsCancel);
        close.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.False(second.IsVisible);
        Assert.All(_spy.Timers, t => Assert.False(t.IsEnabled));
    }

    /// <summary>Платформа окна, которая запоминает созданные таймеры (остальное — как у настоящей).</summary>
    public class TimerSpy : DispatchProxy
    {
        private IUiPlatform _inner = null!;
        public List<IUiTimer> Timers { get; } = [];

        public static TimerSpy Wrap(IUiPlatform inner)
        {
            var proxy = Create<IUiPlatform, TimerSpy>();
            var spy = (TimerSpy)(object)proxy;
            spy._inner = inner;
            return spy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = targetMethod!.Invoke(_inner, args);
            if (result is IUiTimer timer) Timers.Add(timer);
            return result;
        }
    }
}

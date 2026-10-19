using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Settings;
using eViSTool.Desktop.Platform;
using eViSTool.Desktop.Views;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Раздел «Сервер» и карточка профиля внизу боковой панели с настоящей главной моделью: серверный профиль «Стенд» во
/// временной папке, агента нет — сервер остановлен. Вместо пустой консоли — «Сервер остановлен» и «Запустить», плитки без
/// значений приглушены и без прочерков; «дышащая» точка — только пока сервер работает, и кольцо вокруг неё правда
/// двигается. Состояние сервера подаём сами через свойства раздела. Настройки программы — как в
/// <see cref="ProfilesSettingsViewTests"/>: на время теста свои в папке data рядом с тестами, потом прежние.
/// </summary>
public sealed class ServerShellTests : IDisposable
{
    private const string ReleasesUrl = "EVISTOOL_RELEASES_URL";

    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "evistool-tests", "shell-" + Guid.NewGuid().ToString("N"));
    private readonly string _language = Loc.Instance.Language;
    private readonly IUiPlatform? _oldPlatform = Ui.IsSet ? Ui.Platform : null;
    private readonly List<ResourceInclude> _theme = [];
    private CleanupWindowTests.TimerSpy? _timers;
    private StyleInclude? _themeStyles;
    private string? _savedSettings;
    private Window? _window;

    public void Dispose()
    {
        _window?.Close();
        // до возврата настроек: опрос, оставшийся с этого теста, мог бы записать их поверх
        foreach (var timer in _timers?.Timers ?? []) timer.Stop();
        if (_timers is not null) Ui.Platform = _oldPlatform ?? new AvaloniaUiPlatform();
        if (Application.Current is { } app)
        {
            foreach (var include in _theme) app.Resources.MergedDictionaries.Remove(include);
            if (_themeStyles is not null) app.Styles.Remove(_themeStyles);
        }
        if (_timers is null) return; // тест без главной модели: настроек и папок не трогал
        Loc.Instance.SetLanguage(_language);
        try
        {
            if (_savedSettings is null) File.Delete(AppPaths.SettingsFile);
            else File.WriteAllText(AppPaths.SettingsFile, _savedSettings);
        }
        catch (IOException) { }
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>Главная модель над своими настройками: один серверный профиль «Стенд» во временной папке, без автопроверки модов.</summary>
    private MainViewModel Main()
    {
        Assert.SkipUnless(AppPaths.IsPortable, "данные рядом с тестами недоступны для записи — настоящие данные пользователя не трогаем");
        var app = Application.Current!;
        foreach (var name in new[] { "ForestResources", "ForestControls", "ForestWindowChrome" })
        {
            var include = new ResourceInclude(new Uri("avares://eViSTool.Desktop.Tests")) { Source = new Uri($"avares://eViSTool/Themes/{name}.axaml") };
            _theme.Add(include);
            app.Resources.MergedDictionaries.Add(include);
        }
        _themeStyles = new StyleInclude(new Uri("avares://eViSTool.Desktop.Tests")) { Source = new Uri("avares://eViSTool/Themes/Forest.axaml") };
        app.Styles.Add(_themeStyles);
        _timers = CleanupWindowTests.TimerSpy.Wrap(new AvaloniaUiPlatform());
        Ui.Platform = (IUiPlatform)(object)_timers;
        _savedSettings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllText(AppPaths.SettingsFile) : null;

        Directory.CreateDirectory(System.IO.Path.Combine(_dir, "game"));
        Directory.CreateDirectory(System.IO.Path.Combine(_dir, "data"));
        var profile = new GameProfile
        {
            Id = "shell-test", Name = "Стенд", Kind = ProfileKind.Server,
            GameDir = System.IO.Path.Combine(_dir, "game"), DataDir = System.IO.Path.Combine(_dir, "data"),
        };
        new SettingsStore().Save(new AppSettings
        {
            Profiles = [profile], ActiveProfileId = profile.Id, Language = _language, AutoCheckUpdates = false, GameBackground = false,
        });

        // тихая проверка новой версии — не на GitHub, а в закрытый порт: отказ — как «нет сети»
        var releases = Environment.GetEnvironmentVariable(ReleasesUrl);
        Environment.SetEnvironmentVariable(ReleasesUrl, "http://127.0.0.1:9/releases");
        try { return new MainViewModel(); }
        finally { Environment.SetEnvironmentVariable(ReleasesUrl, releases); }
    }

    private Window Show(Window window)
    {
        _window = window;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Control Named(Visual root, string automationId) =>
        root.GetVisualDescendants().OfType<Control>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    private static Color Token(Window window, string key) =>
        window.TryFindResource(key, window.ActualThemeVariant, out var value) && value is ISolidColorBrush brush ? brush.Color
            : throw new KeyNotFoundException(key);

    /// <summary>Дать часам анимаций пройти <paramref name="ms"/> миллисекунд: тик отрисовки каждые ~10 мс (окно без экрана само не тикает).</summary>
    private static async Task Run(int ms)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        } while (watch.ElapsedMilliseconds < ms);
    }

    [AvaloniaFact]
    public async Task Stopped_server_without_lines_shows_stopped_and_start_instead_of_an_empty_console()
    {
        var main = Main();
        var server = main.Server;
        var window = Show(new Window { Width = 1200, Height = 860, Content = new ServerView { DataContext = server } });

        // заглушка — после первого прохода слежения: до него неизвестно, остановлен ли сервер
        var empty = Named(window, "SrvConsoleEmpty");
        await Wait.Until(() => empty.IsEffectivelyVisible);
        Assert.Equal(Loc.T("server.emptyStopped"), ((TextBlock)Named(window, "SrvConsoleEmptyTitle")).Text);
        var start = (Button)Named(window, "SrvConsoleEmptyStart");
        Assert.True(start.IsEffectivelyVisible);
        Assert.True(start.IsEffectivelyEnabled);
        Assert.Same(server.StartCommand, start.Command); // та же «Запустить», что сверху
        Assert.False(Named(window, "SrvConsoleEmptyNote").IsEffectivelyVisible); // запустить можно — причины нет

        // плитки до запуска: пустые и приглушённые, без прочерков и без «PID —»
        var faint = Token(window, "Forest.Faint");
        foreach (var id in new[] { "SrvUptime", "SrvMemory", "SrvPlayers" })
        {
            var tile = Named(window, id);
            Assert.Contains("idle", tile.Classes);
            var texts = tile.GetVisualDescendants().OfType<TextBlock>().ToList();
            Assert.Equal(faint, Assert.IsAssignableFrom<ISolidColorBrush>(texts.Single(t => t.Classes.Contains("tileLabel")).Foreground).Color);
            Assert.DoesNotContain(texts, t => t.IsEffectivelyVisible && t.Text?.Contains('—') == true);
        }
        Assert.False(Named(window, "SrvPid").IsEffectivelyVisible);

        // строки есть — обычная консоль, даже у остановленного сервера
        server.Lines.Add(new ConsoleLineViewModel(new ConsoleLine(1, DateTime.Now, ConsoleLineKind.Output, "Server stopped")));
        Dispatcher.UIThread.RunJobs();
        Assert.False(empty.IsEffectivelyVisible);
        Assert.Equal(1, ((ListBox)Named(window, "SrvConsole")).ItemCount);

        // сервер работает — тоже консоль, хотя строк ещё нет; у плиток со значением — обычная подпись
        server.Lines.Clear();
        server.AgentRunning = true;
        server.State = ServerState.Running;
        server.MemoryText = Loc.T("server.memoryMb", "1 024");
        server.PidText = "42";
        Dispatcher.UIThread.RunJobs();
        Assert.False(empty.IsEffectivelyVisible);
        Assert.DoesNotContain("idle", Named(window, "SrvMemory").Classes);
        Assert.True(Named(window, "SrvPid").IsEffectivelyVisible);

        // остановили, строки убрали — заглушка снова
        server.State = ServerState.Stopped;
        Dispatcher.UIThread.RunJobs();
        Assert.True(empty.IsEffectivelyVisible);
    }

    /// <summary>
    /// Главное окно с моделью без показа, как в <see cref="MainWindowBackdropTests"/>: привязкам и стилям хватает прохода
    /// раскладки, а «дыхание» само по себе проверяется на отдельном показанном окне (кольцо в карточке — то же).
    /// </summary>
    private static MainWindow Unshown(MainViewModel main)
    {
        var window = new MainWindow { DataContext = main, Width = 1400, Height = 860 };
        window.Measure(window.ClientSize);
        window.Arrange(new Rect(window.ClientSize));
        return window;
    }

    [AvaloniaFact]
    public void Breathing_dot_in_the_profile_card_only_while_the_server_runs()
    {
        var main = Main();
        var window = Unshown(main);
        // окно не показано, поэтому видимость — самой строки (родители у неё видимые всегда)
        var row = Named(window, "ServerRunning");
        var ring = window.FindControl<Ellipse>("ServerRunningRing")!;
        Assert.False(row.IsVisible); // сервер остановлен — карточка как раньше, без точки
        Assert.DoesNotContain("breathing", ring.Classes);

        main.Server.AgentRunning = true;
        main.Server.State = ServerState.Starting;
        Assert.False(row.IsVisible); // запускается — ещё не «работает»
        Assert.DoesNotContain("breathing", ring.Classes);

        main.Server.State = ServerState.Running;
        Assert.True(row.IsVisible);
        Assert.Contains("breathing", ring.Classes);
        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("shell.serverRunning"));
        Assert.Same(window.FindResource("Forest.Good"), ring.Fill);

        // остановили — точки нет, и кольцо больше не дышит (класс снят)
        main.Server.State = ServerState.Stopped;
        Assert.False(row.IsVisible);
        Assert.DoesNotContain("breathing", ring.Classes);
    }

    /// <summary>Кольцо с классом breathing в окне с лесной темой — как в карточке профиля: точка 7 px и кольцо над ней.</summary>
    private static (Window Window, Ellipse Ring, Ellipse Dot) BreathingRing()
    {
        var host = new Uri("avares://eViSTool.Desktop.Tests");
        var ring = new Controls.BreathingRing { Width = 7, Height = 7, Opacity = 0, Classes = { "breathing" } };
        var dot = new Ellipse { Width = 7, Height = 7 };
        var window = new Window { Width = 200, Height = 120, Content = new Panel { Width = 7, Height = 7, Children = { ring, dot } } };
        window.Resources.MergedDictionaries.Add(new ResourceInclude(host) { Source = new Uri("avares://eViSTool/Themes/ForestResources.axaml") });
        window.Styles.Add(new StyleInclude(host) { Source = new Uri("avares://eViSTool/Themes/Forest.axaml") });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, ring, dot);
    }

    [AvaloniaFact]
    public async Task Breathing_ring_grows_and_fades_by_transform_and_opacity_and_stops_without_the_class()
    {
        var (window, ring, dot) = BreathingRing();
        _window = window;
        // стиль кольца не трогает обычные Ellipse: иначе встаёт, например, ручка переключателя (её сдвигает стиль темы)
        Assert.False(dot.IsSet(Visual.RenderTransformProperty));

        // кольцо дышит: масштаб и прозрачность меняются по кругу, кольцо не меньше точки и не ярче начала вдоха
        var scales = new HashSet<double>();
        var opacities = new HashSet<double>();
        for (var i = 0; i < 20; i++)
        {
            await Run(100);
            var scale = (ring.RenderTransform?.Value ?? Matrix.Identity).M11;
            Assert.InRange(scale, 0.999, 2.601);
            Assert.InRange(ring.Opacity, 0, 0.551);
            scales.Add(Math.Round(scale, 2));
            opacities.Add(Math.Round(ring.Opacity, 2));
        }
        Assert.True(scales.Count > 3, $"масштаб кольца: {string.Join(", ", scales)}");
        Assert.True(opacities.Count > 3, $"прозрачность кольца: {string.Join(", ", opacities)}");
        Assert.Equal(ring.Width, ring.Bounds.Width); // раскладка не трогается: растёт только отрисовка

        Assert.False(dot.IsSet(Visual.RenderTransformProperty));

        // класс снят — анимации нет: кольцо гаснет до своего значения и стоит
        ring.Classes.Remove("breathing");
        await Run(100);
        Assert.Equal(0, ring.Opacity);
        var still = (ring.RenderTransform?.Value ?? Matrix.Identity).M11;
        await Run(300);
        Assert.Equal(still, (ring.RenderTransform?.Value ?? Matrix.Identity).M11);
    }
}

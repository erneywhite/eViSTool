using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.AppUpdate;
using eViSTool.Core.Localization;
using eViSTool.Core.Versioning;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Окно «Вышла новая версия»: цвет номера рекомендуемой версии,
/// черта над всеми версиями, кроме первой, ссылка у версии без текста, запрет закрытия посреди установки, Enter и Escape.
/// </summary>
public sealed class UpdateWindowTests
{
    private const string Notes = "**eViSTool 9.9.9: faster.**\n\n**New**\n- Faster catalog.\n\n---\n\nБыстрее.\n\n**Новое**\n- Быстрый каталог.";

    private static AppRelease Release(string version, string notes) => new()
    {
        Version = ModVersion.ParseOrNull(version)!,
        Tag = "v" + version,
        PageUrl = "https://example.invalid/releases/v" + version,
        Notes = notes,
        AssetName = "a.zip",
        AssetUrl = "https://example.invalid/a.zip",
    };

    private sealed record Opened(UpdateWindow Window, AboutViewModel About, List<string> Log, Action Restore);

    /// <summary>Окно с лесной темой (ресурсы — в приложение: StaticResource ищет их уже при загрузке разметки).</summary>
    private static Opened Open(bool recommended, Func<Task<AppRelease?>>? install = null)
    {
        var app = Application.Current!;
        var baseUri = new Uri("avares://eViSTool.Desktop.Tests");
        var resources = new ResourceInclude(baseUri) { Source = new Uri("avares://eViSTool/Themes/ForestResources.axaml") };
        var controls = new ResourceInclude(baseUri) { Source = new Uri("avares://eViSTool/Themes/ForestControls.axaml") };
        var styles = new StyleInclude(baseUri) { Source = new Uri("avares://eViSTool/Themes/Forest.axaml") };
        app.Resources.MergedDictionaries.Add(resources);
        app.Resources.MergedDictionaries.Add(controls);
        app.Styles.Add(styles);
        void Restore()
        {
            app.Styles.Remove(styles);
            app.Resources.MergedDictionaries.Remove(controls);
            app.Resources.MergedDictionaries.Remove(resources);
        }

        var about = new AboutViewModel();
        about.UseReleases([Release("9.9.9", Notes), Release("9.9.8", (recommended ? "<!-- evistool: recommended -->\n" : "") + Notes), Release("9.9.7", "")]);
        var log = new List<string>();
        var model = new UpdateWindowViewModel(about, () => log.Add("skip"),
            install ?? (() => Task.FromResult<AppRelease?>(null)), exe => log.Add("restart"), url => log.Add("open " + url));
        model.CloseRequested += () => log.Add("close");
        var window = new UpdateWindow { DataContext = model, RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new Opened(window, about, log, Restore);
    }

    private static T ById<T>(Visual root, string id) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);

    private static Color Resource(string key) => ((ISolidColorBrush)Application.Current!.FindResource(ThemeVariant.Dark, key)!).Color;

    private static Run NewVersionRun(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().SelectMany(t => t.Inlines ?? []).OfType<Run>().Single(r => r.Classes.Contains("newVersion"));

    [AvaloniaFact]
    public void Usual_update_shows_skip_and_sand_version()
    {
        var o = Open(recommended: false);
        try
        {
            Assert.Equal(Loc.T("updwin.title"), o.Window.Title);
            Assert.Equal("9.9.9", NewVersionRun(o.Window).Text);
            Assert.Equal(Resource("Forest.Accent"), ((ISolidColorBrush)NewVersionRun(o.Window).Foreground!).Color);
            Assert.False(ById<Border>(o.Window, "UpdateWindowRecommended").IsVisible);
            Assert.True(ById<Button>(o.Window, "UpdateWindowSkip").IsVisible);
            Assert.False(ById<TextBlock>(o.Window, "UpdateWindowStatus").IsVisible);
        }
        finally
        {
            if (o.Window.IsVisible) o.Window.Close();
            o.Restore();
        }
    }

    [AvaloniaFact]
    public void Recommended_update_hides_skip_and_colors_version_warm()
    {
        var o = Open(recommended: true);
        try
        {
            Assert.Equal(Resource("Forest.Warn"), ((ISolidColorBrush)NewVersionRun(o.Window).Foreground!).Color);
            Assert.True(ById<Border>(o.Window, "UpdateWindowRecommended").IsVisible);
            Assert.False(ById<Button>(o.Window, "UpdateWindowSkip").IsVisible);
        }
        finally
        {
            if (o.Window.IsVisible) o.Window.Close();
            o.Restore();
        }
    }

    [AvaloniaFact]
    public void Versions_are_separated_except_the_first_and_empty_one_links_to_its_page()
    {
        var o = Open(recommended: false);
        try
        {
            var notes = ById<ItemsControl>(o.Window, "UpdateWindowNotes");
            var rows = notes.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("release")).ToList();
            Assert.Equal(3, rows.Count);
            Assert.Equal(default, rows[0].BorderThickness);
            Assert.Equal(default, rows[0].Padding);
            Assert.Equal(new Thickness(0, 1, 0, 0), rows[1].BorderThickness);
            Assert.Equal(new Thickness(0, 14, 0, 0), rows[2].Padding);

            // ссылка «на странице релиза» — только у версии без текста, и ведёт на её страницу
            var link = notes.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible);
            Assert.Same(rows[2], link.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("release")));
            link.Command!.Execute(link.CommandParameter);
            Assert.Equal(["open https://example.invalid/releases/v9.9.7"], o.Log);
        }
        finally
        {
            if (o.Window.IsVisible) o.Window.Close();
            o.Restore();
        }
    }

    [AvaloniaFact]
    public void Cannot_close_while_installing()
    {
        var o = Open(recommended: false);
        try
        {
            o.About.IsUpdating = true;
            o.Window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(o.Window.IsVisible);
            o.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.True(o.Window.IsVisible);
            Assert.False(ById<Button>(o.Window, "UpdateWindowLater").IsEffectivelyEnabled);

            o.About.IsUpdating = false;
            o.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(o.Window.IsVisible);
        }
        finally
        {
            o.About.IsUpdating = false;
            if (o.Window.IsVisible) o.Window.Close();
            o.Restore();
        }
    }

    [AvaloniaFact]
    public void Enter_installs_and_success_closes_the_window_then_restarts()
    {
        var installs = 0;
        var o = Open(recommended: false, () =>
        {
            installs++;
            return Task.FromResult<AppRelease?>(Release("9.9.9", ""));
        });
        try
        {
            o.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, installs);
            Assert.Equal(["close", "restart"], o.Log);
            Assert.False(o.Window.IsVisible);
        }
        finally
        {
            if (o.Window.IsVisible) o.Window.Close();
            o.Restore();
        }
    }

    [AvaloniaFact]
    public void Later_closes_and_status_line_follows_about()
    {
        var o = Open(recommended: false);
        try
        {
            o.About.UpdateStatus = "Скачиваю… 40%";
            var status = ById<TextBlock>(o.Window, "UpdateWindowStatus");
            Assert.True(status.IsVisible);
            Assert.Equal("Скачиваю… 40%", status.Text);

            ById<Button>(o.Window, "UpdateWindowLater").Command!.Execute(null);
            Assert.False(o.Window.IsVisible);
            Assert.Equal(["close"], o.Log);
        }
        finally
        {
            if (o.Window.IsVisible) o.Window.Close();
            o.Restore();
        }
    }
}

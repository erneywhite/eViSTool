using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Окно «Восстановить мир» сервера у хостера с настоящей разметкой и лесной темой: дата и размер копии, предупреждение,
/// галочка «и настройки модов…» (недоступна без архива), «Восстановить» — ответ, «Отмена», Enter и Escape — без ответа.
/// </summary>
public sealed class HostedRestoreWindowTests : IDisposable
{
    private readonly ResourceInclude _resources = new(new Uri("avares://eViSTool.Desktop.Tests"))
    {
        Source = new Uri("avares://eViSTool/Themes/ForestResources.axaml"),
    };

    private readonly ResourceInclude _controls = new(new Uri("avares://eViSTool.Desktop.Tests"))
    {
        Source = new Uri("avares://eViSTool/Themes/ForestControls.axaml"),
    };

    private readonly StyleInclude _styles = new(new Uri("avares://eViSTool.Desktop.Tests"))
    {
        Source = new Uri("avares://eViSTool/Themes/Forest.axaml"),
    };

    public HostedRestoreWindowTests()
    {
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
    }

    private static readonly DateTime Made = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Local);

    private static HostedWorldBackup Own => new(@"C:\b\Survival-2026-10-18_09-00-00.vcdbs", "Survival-2026-10-18_09-00-00.vcdbs", Made, 90L << 20)
    {
        IsStamped = true, IsOwn = true, ExtraSize = 1L << 20, Origin = HostedWorldOrigin.World, SourcePath = "/Saves/MyVintageStoryWorld.vcdbs",
    };

    private static HostedWorldBackup Manual => new(@"C:\b\old-world.vcdbs", "old-world.vcdbs", Made, 50L << 20);

    private static (HostedRestoreWindow Window, HostedRestoreViewModel Model) Open(HostedWorldBackup backup)
    {
        var model = new HostedRestoreViewModel(backup, "Survival");
        var window = new HostedRestoreWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, model);
    }

    private static T Named<T>(Window window, string automationId) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    private static void Press(Window window, Key key) =>
        window.KeyPress(key, RawInputModifiers.None, key == Key.Enter ? PhysicalKey.Enter : PhysicalKey.Escape, null);

    [AvaloniaFact]
    public void Own_copy_shows_its_date_and_size_and_restores_with_mod_settings()
    {
        var (window, model) = Open(Own);

        Assert.Equal(Loc.T("hostedrst.heading", "Survival"), Named<TextBlock>(window, "HostedRestoreHeading").Text);
        Assert.Equal(Loc.T("hostedrst.copy", Dates.Stamp(Made), Sizes.Format(91L << 20), Loc.T("hostedbk.originWorld")),
            Named<TextBlock>(window, "HostedRestoreCopy").Text);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("hostedrst.warning") && t.IsEffectivelyVisible);
        Assert.False(Named<TextBlock>(window, "HostedRestoreForeign").IsVisible);
        var extras = Named<CheckBox>(window, "HostedRestoreExtras");
        Assert.True(extras.IsEffectivelyEnabled);
        Assert.True(extras.IsChecked); // данные модов — часть мира: по умолчанию вместе с ним

        Mouse.Click(window, Named<Button>(window, "HostedRestoreOk"));

        Assert.False(window.IsVisible);
        Assert.Equal(new HostedRestoreChoice(true), model.Result);
    }

    [AvaloniaFact]
    public void Unticked_box_restores_only_the_world()
    {
        var (window, model) = Open(Own);

        Mouse.Click(window, Named<CheckBox>(window, "HostedRestoreExtras"), x: 8);
        Assert.False(model.Extras);
        Mouse.Click(window, Named<Button>(window, "HostedRestoreOk"));

        Assert.Equal(new HostedRestoreChoice(false), model.Result);
    }

    [AvaloniaFact]
    public void Manual_copy_without_archive_warns_and_offers_no_box_and_Enter_cancels()
    {
        var (window, model) = Open(Manual);

        var foreign = Named<TextBlock>(window, "HostedRestoreForeign");
        Assert.True(foreign.IsEffectivelyVisible);
        Assert.Equal(Loc.T("hostedrst.manual"), foreign.Text);
        var extras = Named<CheckBox>(window, "HostedRestoreExtras");
        Assert.False(extras.IsEffectivelyEnabled);
        Assert.False(extras.IsChecked);
        Assert.Equal(Loc.T("hostedrst.noExtras"), Named<TextBlock>(window, "HostedRestoreExtrasHint").Text);

        Press(window, Key.Enter); // случайный Enter мир на сервере не меняет

        Assert.False(window.IsVisible);
        Assert.Null(model.Result);
    }

    [AvaloniaFact]
    public void Cancel_and_Escape_close_without_answer()
    {
        var (window, model) = Open(Own);
        Mouse.Click(window, Named<Button>(window, "HostedRestoreCancel"));
        Assert.False(window.IsVisible);
        Assert.Null(model.Result);

        (window, model) = Open(Own);
        Press(window, Key.Escape);
        Assert.False(window.IsVisible);
        Assert.Null(model.Result);
    }
}

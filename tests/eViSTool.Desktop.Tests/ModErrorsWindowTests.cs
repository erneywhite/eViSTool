using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Diagnostics;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Окно «Ошибки модов»: обычное (не поверх всех, не в панели задач), кнопка «Выключить мод» по строке модели и после
/// нажатия, «Скрыть» и «Страница мода» закрывают окно по просьбе модели, Escape и «Закрыть», длинный список не растит окно.
/// </summary>
public sealed class ModErrorsWindowTests
{
    private static readonly GameProfile Server = new() { Id = "server-1", Name = "Сервер", Kind = ProfileKind.Server };

    private sealed class Host : IModErrorsHost
    {
        public List<string> Calls { get; } = [];
        public Task ShowInCatalogAsync(string modId, string? name)
        {
            Calls.Add("catalog " + modId);
            return Task.CompletedTask;
        }
        public void DismissErrorReport(GameProfile profile, ModErrorReport report) => Calls.Add("dismiss");
        public void SetClipboard(string text) => Calls.Add("copy");
    }

    /// <summary>Выключатель без диска и агента: цель есть, выключение удачно.</summary>
    private static ModDisabler Disabler() =>
        new(_ => false, (_, _) => { }, _ => { }, p => new ModTarget(p, null), (_, _) => Task.CompletedTask);

    private static ModErrorLine Line(string id, string? path, int count = 12) =>
        new(id, id + " mod", "1.0.0", path, count, $"[{id}] System.NullReferenceException: boom", CulpritSource.Message);

    private static ModErrorReport Report(params ModErrorLine[] mods) => new()
    {
        At = new DateTime(2026, 10, 9, 21, 14, 0),
        Total = mods.Sum(m => m.Count),
        Mods = mods,
    };

    private static (ModErrorsWindow Window, Host Host) Open(ModErrorReport report)
    {
        var host = new Host();
        var window = new ModErrorsWindow { DataContext = new ModErrorsViewModel(Server, report, Disabler(), host) };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, host);
    }

    private static List<T> All<T>(Window window, string automationId) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Where(c => AutomationProperties.GetAutomationId(c) == automationId).ToList();

    private static void Click(Window window, Control button)
    {
        // щелчок мышью: команда из привязки выполняется только настоящим нажатием
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
    }

    [AvaloniaFact]
    public void Opens_AsOrdinaryWindow_WithRowsFromModel()
    {
        var (window, _) = Open(Report(Line("a", @"C:\Mods\a.zip"), Line("b", null)));
        Assert.False(window.Topmost);
        Assert.False(window.ShowInTaskbar);
        Assert.True(window.CanResize);
        Assert.Equal(SizeToContent.Height, window.SizeToContent);

        var disable = All<Button>(window, "ErrsDisable");
        Assert.Equal(2, disable.Count);
        Assert.True(disable[0].IsEffectivelyEnabled);
        Assert.False(disable[1].IsEffectivelyEnabled); // мод не нашёлся среди установленных
        Assert.All(All<Button>(window, "ErrsCatalog"), b => Assert.True(b.IsEffectivelyEnabled));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Disable_ShowsStatus_AndButtonStaysUnavailable()
    {
        var (window, _) = Open(Report(Line("a", @"C:\Mods\a.zip"), Line("b", @"C:\Mods\b.zip")));
        var disable = All<Button>(window, "ErrsDisable");

        Click(window, disable[0]);

        var status = All<TextBlock>(window, "ErrsStatus").Single();
        await Wait.Until(() => !string.IsNullOrEmpty(status.Text));
        Assert.False(disable[0].IsEffectivelyEnabled);
        Assert.True(disable[1].IsEffectivelyEnabled); // соседняя строка — своя кнопка
        window.Close();
    }

    [AvaloniaFact]
    public void Dismiss_ClosesWindow()
    {
        var (window, host) = Open(Report(Line("a", @"C:\Mods\a.zip")));
        Click(window, All<Button>(window, "ErrsDismiss").Single());
        Assert.Equal(["dismiss"], host.Calls);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void Catalog_OpensRowMod_AndClosesWindow()
    {
        var (window, host) = Open(Report(Line("a", @"C:\Mods\a.zip"), Line("b", null)));
        Click(window, All<Button>(window, "ErrsCatalog")[1]);
        Assert.Equal(["catalog b"], host.Calls);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void Escape_And_Close_CloseTheWindow()
    {
        var report = Report(Line("a", @"C:\Mods\a.zip"));
        var (byEscape, _) = Open(report);
        byEscape.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(byEscape.IsVisible);

        var (byButton, host) = Open(report);
        All<Button>(byButton, "ErrsClose").Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(byButton.IsVisible);
        Assert.Empty(host.Calls); // «Закрыть» отчёт не скрывает
    }

    // модов много — список прокручивается, окно не выше предела (в окне на WPF — то же ограничение списка)
    [AvaloniaFact]
    public void ManyMods_ListScrolls_WindowStaysBounded()
    {
        var (window, _) = Open(Report([.. Enumerable.Range(1, 20).Select(i => Line("mod" + i, $@"C:\Mods\mod{i}.zip", 30 - i))]));
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Content is ItemsControl);
        Assert.True(scroll.Bounds.Height <= 520, $"список {scroll.Bounds.Height}");
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "список не прокручивается");
        Assert.True(window.Bounds.Height <= window.MaxHeight, $"окно {window.Bounds.Height}");
        window.Close();
    }
}

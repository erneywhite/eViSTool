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
/// нажатия, «Страница мода» закрывает окно по просьбе модели, «Игнорировать» и «Вернуть» переносят строку, Escape и
/// «Закрыть», длинный список не растит окно.
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
        public void SaveErrorIgnores(GameProfile profile, ModErrorIgnores ignores) => Calls.Add("ignores");
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
        var window = new ModErrorsWindow { DataContext = new ModErrorsViewModel(Server, report, new ModErrorIgnores(), Disabler(), host) };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, host);
    }

    private static List<T> All<T>(Window window, string automationId) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Where(c => AutomationProperties.GetAutomationId(c) == automationId).ToList();

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

        Mouse.Click(window, disable[0]);

        var status = All<TextBlock>(window, "ErrsStatus").Single();
        await Wait.Until(() => !string.IsNullOrEmpty(status.Text));
        Assert.False(disable[0].IsEffectivelyEnabled);
        Assert.True(disable[1].IsEffectivelyEnabled); // соседняя строка — своя кнопка
        window.Close();
    }

    // «Игнорировать» уносит строку в свёрнутый список внизу, «Вернуть» — обратно; окно остаётся открытым
    [AvaloniaFact]
    public void Ignore_MovesRowDown_Restore_BringsItBack()
    {
        var (window, host) = Open(Report(Line("a", @"C:\Mods\a.zip"), Line("b", @"C:\Mods\b.zip")));
        var ignored = All<Expander>(window, "ErrsIgnored").Single();
        Assert.False(ignored.IsVisible);

        Mouse.Click(window, All<Button>(window, "ErrsIgnore")[0]);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(All<Button>(window, "ErrsIgnore"));
        Assert.True(ignored.IsVisible);
        Assert.True(window.IsVisible);

        ignored.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        Mouse.Click(window, All<Button>(window, "ErrsRestore").Single());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, All<Button>(window, "ErrsIgnore").Count);
        Assert.False(ignored.IsVisible);
        Assert.Equal(["ignores", "ignores"], host.Calls);
        window.Close();
    }

    // всё в игноре — вместо списка строка «новых ошибок нет»; у ошибок самой игры нет «Выключить» и «Страницы мода»
    [AvaloniaFact]
    public void GameRow_OnlyIgnore_AndAllIgnored_SaysSo()
    {
        var report = Report(Line("a", @"C:\Mods\a.zip")) with { Total = 17, Unattributed = 5, UnattributedExample = "boom" };
        var (window, _) = Open(report);
        Assert.Equal(2, All<Button>(window, "ErrsIgnore").Count);
        Assert.Equal(1, All<Button>(window, "ErrsDisable").Count(b => b.IsVisible));
        Assert.Equal(1, All<Button>(window, "ErrsCatalog").Count(b => b.IsVisible));
        var none = All<TextBlock>(window, "ErrsNoneActive").Single();
        Assert.False(none.IsVisible);

        Mouse.Click(window, All<Button>(window, "ErrsIgnore")[1]);
        Dispatcher.UIThread.RunJobs();
        Mouse.Click(window, All<Button>(window, "ErrsIgnore")[0]);
        Dispatcher.UIThread.RunJobs();
        Assert.True(none.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Catalog_OpensRowMod_AndClosesWindow()
    {
        var (window, host) = Open(Report(Line("a", @"C:\Mods\a.zip"), Line("b", null)));
        Mouse.Click(window, All<Button>(window, "ErrsCatalog")[1]);
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

    // модов много — список прокручивается, окно не выше предела
    [AvaloniaFact]
    public void ManyMods_ListScrolls_WindowStaysBounded()
    {
        var (window, _) = Open(Report([.. Enumerable.Range(1, 20).Select(i => Line("mod" + i, $@"C:\Mods\mod{i}.zip", 30 - i))]));
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Content is StackPanel p && p.Children.OfType<ItemsControl>().Any());
        Assert.True(scroll.Bounds.Height <= 520, $"список {scroll.Bounds.Height}");
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "список не прокручивается");
        Assert.True(window.Bounds.Height <= window.MaxHeight, $"окно {window.Bounds.Height}");
        window.Close();
    }
}

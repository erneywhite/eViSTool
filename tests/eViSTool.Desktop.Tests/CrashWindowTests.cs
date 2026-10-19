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
using eViSTool.Core.Profiles;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>Окно вылета: поверх всех и в панели задач, кнопки по модели, стек в несколько строк, Escape и «Закрыть».</summary>
public sealed class CrashWindowTests
{
    private sealed class Host : ICrashWindowHost
    {
        public void SwitchTo(GameProfile profile, int tab) { }
        public void SearchMods(string text) { }
        public Task ShowInCatalogAsync(string modId, string? name) => Task.CompletedTask;
        public void ActivateMainWindow() { }
        public void SetClipboard(string text) { }
    }

    private static CrashView View(string? modId = "carryon", bool installed = true) =>
        new("Игра вылетела", "Игра", modId, "Carry On", "1.8.0", CulpritSource.Stack, installed, "Game crashed", 1,
            "System.NullReferenceException: boom", ["CarryOn.Common.CarrySystem.OnTick", "Vintagestory.Client.NoObf.ClientMain.MainGameLoop"]);

    private static CrashWindow Open(CrashWindowViewModel model)
    {
        var window = new CrashWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Control Named(Window window, string automationId) =>
        window.GetVisualDescendants().OfType<Control>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    [AvaloniaFact]
    public void Opens_OnTop_InTaskbar_WithModelTexts()
    {
        var window = Open(new CrashWindowViewModel(View(), () => Task.FromResult(new ModDisableResult("x", false)), () => { }, "Найти", new Host()));
        Assert.Equal("Игра вылетела", window.Title);
        Assert.True(window.Topmost);
        Assert.True(window.ShowInTaskbar);
        Assert.False(window.CanResize);
        Assert.True(Named(window, "CrashDisable").IsVisible);
        Assert.True(Named(window, "CrashCatalog").IsVisible);
        Assert.Equal("Найти", ((Button)Named(window, "CrashShow")).Content);
        window.Close();
    }

    [AvaloniaFact]
    public void Buttons_FollowModel()
    {
        var window = Open(new CrashWindowViewModel(View(modId: null, installed: false), null, () => { }, "Найти", new Host()));
        Assert.False(Named(window, "CrashDisable").IsVisible);
        Assert.False(Named(window, "CrashCatalog").IsVisible);
        Assert.True(Named(window, "CrashCopy").IsVisible);
        window.Close();
    }

    /// <summary>
    /// Все пять кнопок в строку не влезают: каждая строка прижата к правому краю, «Закрыть» — справа внизу, между строками
    /// и кнопками зазор. Окно уже обычного — чтобы перенос был при любом шрифте.
    /// </summary>
    [AvaloniaFact]
    public void Buttons_WrapToTheRight_WithGaps()
    {
        var window = Open(new CrashWindowViewModel(View(), () => Task.FromResult(new ModDisableResult("x", false)), () => { }, "Найти", new Host()));
        window.Width = 420;
        Dispatcher.UIThread.RunJobs();
        var panel = (WrapPanel)Named(window, "CrashButtons");
        var rows = panel.Children.Where(b => b.IsVisible).GroupBy(b => b.Bounds.Top).OrderBy(r => r.Key).Select(r => r.OrderBy(b => b.Bounds.Left).ToList()).ToList();
        Assert.True(rows.Count > 1, "кнопки не перенеслись");
        foreach (var row in rows)
        {
            Assert.Equal(panel.Bounds.Width, row[^1].Bounds.Right, 1);
            for (var i = 1; i < row.Count; i++) Assert.Equal(8, row[i].Bounds.Left - row[i - 1].Bounds.Right, 1);
        }
        for (var i = 1; i < rows.Count; i++) Assert.True(rows[i][0].Bounds.Top - rows[i - 1].Max(b => b.Bounds.Bottom) >= 8 - 0.5, "строки слиплись");
        Assert.Same(Named(window, "CrashClose"), rows[^1][^1]);
        window.Close();
    }

    // стек — в несколько строк: TextBox без AcceptsReturn сложил бы его в одну
    [AvaloniaFact]
    public void Details_KeepLines()
    {
        var model = new CrashWindowViewModel(View(), null, () => { }, "Найти", new Host());
        var window = Open(model);
        window.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        var details = (TextBox)Named(window, "CrashDetails");
        Assert.True(details.AcceptsReturn);
        Assert.True(details.IsReadOnly);
        Assert.Equal(model.Details, details.Text);
        Assert.True(details.Bounds.Height > 3 * details.FontSize, $"стек в одну строку: высота {details.Bounds.Height}");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Disable_ShowsStatus()
    {
        var window = Open(new CrashWindowViewModel(View(), () => Task.FromResult(new ModDisableResult("Мод выключен", false)), () => { }, "Найти", new Host()));
        var disable = (Button)Named(window, "CrashDisable");
        // щелчок мышью по кнопке: команда из привязки выполняется только настоящим нажатием, не событием Click
        Mouse.Click(window, disable);
        await Wait.Until(() => ((TextBlock)Named(window, "CrashStatus")).Text == "Мод выключен");
        Assert.False(disable.IsEffectivelyEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void Escape_And_Close_CloseTheWindow()
    {
        var model = new CrashWindowViewModel(View(), null, () => { }, "Найти", new Host());
        var byEscape = Open(model);
        byEscape.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(byEscape.IsVisible);

        var byButton = Open(model);
        Named(byButton, "CrashClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(byButton.IsVisible);
    }
}

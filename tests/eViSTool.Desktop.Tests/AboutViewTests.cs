using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Localization;
using eViSTool.Desktop.Views;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>«О программе»: строка «Можно вернуться на …» и кнопка возврата видны, только пока прежняя версия хранится.</summary>
public sealed class AboutViewTests
{
    private static Window Show(AboutViewModel model)
    {
        var baseUri = new Uri("avares://eViSTool.Desktop.Tests");
        var window = new Window { Width = 1100, Height = 760, RequestedThemeVariant = ThemeVariant.Dark, Content = new AboutView { DataContext = model } };
        window.Resources.MergedDictionaries.Add(new ResourceInclude(baseUri) { Source = new Uri("avares://eViSTool/Themes/ForestResources.axaml") });
        window.Resources.MergedDictionaries.Add(new ResourceInclude(baseUri) { Source = new Uri("avares://eViSTool/Themes/ForestControls.axaml") });
        window.Styles.Add(new StyleInclude(baseUri) { Source = new Uri("avares://eViSTool/Themes/Forest.axaml") });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Named<T>(Window window, string automationId) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    [AvaloniaFact]
    public void RollbackLineAndButton_OnlyWhileThePreviousVersionIsKept()
    {
        var model = new AboutViewModel();
        var window = Show(model);
        var button = Named<Button>(window, "RollBack");
        Assert.False(button.IsEffectivelyVisible);

        model.PreviousVersion = "0.14.0";
        Dispatcher.UIThread.RunJobs();

        Assert.True(button.IsEffectivelyVisible);
        Assert.Same(model.RollBackCommand, button.Command);
        Assert.Equal(Loc.T("about.rollbackAvailable", "0.14.0"), Named<TextBlock>(window, "PreviousVersion").Text);

        model.PreviousVersion = null;
        Dispatcher.UIThread.RunJobs();
        Assert.False(button.IsEffectivelyVisible);
        window.Close();
    }
}

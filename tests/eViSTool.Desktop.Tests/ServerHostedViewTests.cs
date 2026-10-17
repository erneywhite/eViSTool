using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Tests.Hosting;
using eViSTool.Desktop.Platform;
using eViSTool.Desktop.Views;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Раздел «Сервер» у сервера у хостера: плитки состояния по логу, консоль только для чтения (поля команды нет), игроки
/// рядом, пустая консоль и строка «нет связи». Лог — настоящий лог G-Portal на поддельном FTP.
/// </summary>
public sealed class ServerHostedViewTests : IDisposable
{
    private const string Main = "/Logs/server-main.log";
    private readonly HostedFixture _host = new();

    public ServerHostedViewTests()
    {
        if (!Ui.IsSet) Ui.Platform = new AvaloniaUiPlatform();
        _host.GPortalLayout(config: false, log: false);
    }

    public void Dispose() => _host.Dispose();

    /// <summary>Лог со временем файла, как у G-Portal: последняя строка только что записана, часы сервера — UTC+2.</summary>
    private void PutLog(string[] lines)
    {
        var marks = new HostedRunMarks();
        foreach (var line in lines) marks.Add(line);
        File.SetLastWriteTimeUtc(_host.Server.Put(Main, HostedLogSamples.Bytes(lines)),
            DateTime.SpecifyKind(marks.LastTime!.Value.AddHours(-2), DateTimeKind.Utc));
    }

    private async Task<HostedServerViewModel> PolledAsync()
    {
        var model = new HostedServerViewModel(null!);
        model.OnProfileSwitched(_host.Profile);
        var poll = model.PollAsync();
        await Wait.Until(() => poll.IsCompleted);
        await poll;
        return model;
    }

    /// <summary>Вид в окне с лесной темой (на уровне окна, чтобы не менять приложение тестов для остальных проверок).</summary>
    private static Window Show(HostedServerViewModel model)
    {
        var baseUri = new Uri("avares://eViSTool.Desktop.Tests");
        var window = new Window { Width = 1200, Height = 760, RequestedThemeVariant = ThemeVariant.Dark, Content = new ServerHostedView { DataContext = model } };
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
    public async Task Running_server_shows_the_state_the_log_and_the_player_without_a_command_box()
    {
        PutLog(HostedLogSamples.Running);
        var model = await PolledAsync();
        var window = Show(model);

        Assert.Equal(Loc.T("server.stateRunning"), Named<TextBlock>(window, "HostedStateText").Text);
        Assert.True(Named<TextBlock>(window, "HostedStateNote").IsEffectivelyVisible); // «по логу сервера · …»
        var console = Named<ListBox>(window, "HostedConsole");
        Assert.Equal(HostedLogSamples.Running.Length, console.ItemCount);
        Assert.Contains(console.GetVisualDescendants().OfType<TextBlock>(), t => t.Classes.Contains("line")); // строки — со стилем своей консоли
        Assert.False(Named<TextBlock>(window, "HostedConsoleEmpty").IsEffectivelyVisible);
        // команды — в панели хостера: поля ввода в разделе нет
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible);
        Assert.Contains(Named<ItemsControl>(window, "HostedPlayerList").GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Erney");
        Assert.False(Named<TextBlock>(window, "HostedPlayersEmpty").IsEffectivelyVisible);
        Assert.True(Named<ComboBox>(window, "HostedRuns").IsEffectivelyVisible);
        Assert.False(Named<TextBlock>(window, "HostedStatus").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Console_lines_get_the_colours_of_the_own_console()
    {
        // строки в конце лога: консоль показывает конец, а виртуальный список создаёт только видимые
        string[] log = [.. HostedLogSamples.Running, "17.10.2026 13:31:19 [Error] Something broke", "17.10.2026 13:31:19 [Warning] Server overloaded",
            "17.10.2026 13:31:19 [Event] Nothing special"];
        PutLog(log);
        var model = await PolledAsync();
        var window = Show(model);

        var lines = Named<ListBox>(window, "HostedConsole").GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("line")).ToList();
        Color Of(string text) => ((ISolidColorBrush)lines.Single(t => t.Text?.EndsWith(text, StringComparison.Ordinal) == true).Foreground!).Color;
        Color Resource(string key) =>
            window.TryFindResource(key, window.ActualThemeVariant, out var brush) ? ((ISolidColorBrush)brush!).Color : throw new KeyNotFoundException(key);

        // стили ServerStyles.axaml дошли до строк: цвет по уровню, как у своей консоли
        Assert.Equal(Resource("Forest.Danger"), Of("Something broke"));
        Assert.Equal(Resource("Forest.Warn"), Of("Server overloaded"));
        Assert.Equal(Color.Parse("#D8DCCF"), Of("Nothing special"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task No_log_yet_says_so_and_a_network_error_is_a_status_line()
    {
        _host.Server.MakeDir("/Logs");
        var model = await PolledAsync();
        var window = Show(model);

        Assert.Equal(Loc.T("server.hostedStateUnknown"), Named<TextBlock>(window, "HostedStateText").Text);
        Assert.True(Named<TextBlock>(window, "HostedConsoleEmpty").IsEffectivelyVisible);
        Assert.True(Named<TextBlock>(window, "HostedPlayersEmpty").IsEffectivelyVisible);

        _host.Server.FailOn = (op, _) => op == "list";
        _host.Server.FailKind = RemoteFailure.Timeout;
        var poll = model.PollAsync();
        await Wait.Until(() => poll.IsCompleted);
        Dispatcher.UIThread.RunJobs();

        var status = Named<TextBlock>(window, "HostedStatus");
        Assert.True(status.IsEffectivelyVisible);
        Assert.Equal(Loc.T("server.hostedRetry", Loc.T("hosted.errTimeout"), 10), status.Text);
        window.Close();
    }
}

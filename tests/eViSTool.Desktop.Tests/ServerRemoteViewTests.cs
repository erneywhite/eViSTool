using Avalonia.Automation;
using Avalonia.Styling;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Desktop.Platform;
using eViSTool.Desktop.Views;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>Вкладка «Удалённый доступ»: что показывается при выключенном и включённом доступе, кнопка брандмауэра только на Windows.</summary>
public sealed class ServerRemoteViewTests
{
    public ServerRemoteViewTests()
    {
        // модель ставит таймер показа кода через платформу
        if (!Ui.IsSet) Ui.Platform = new AvaloniaUiPlatform();
    }

    /// <summary>Вкладка в окне с лесной темой (на уровне окна, чтобы не менять приложение тестов для остальных проверок).</summary>
    internal static Window Show(ServerRemoteViewModel model)
    {
        var baseUri = new Uri("avares://eViSTool.Desktop.Tests");
        var window = new Window { Width = 1100, Height = 760, RequestedThemeVariant = ThemeVariant.Dark, DataContext = model, Content = new ServerRemoteView { DataContext = model } };
        window.Resources.MergedDictionaries.Add(new ResourceInclude(baseUri) { Source = new Uri("avares://eViSTool.Desktop/Themes/ForestResources.axaml") });
        window.Resources.MergedDictionaries.Add(new ResourceInclude(baseUri) { Source = new Uri("avares://eViSTool.Desktop/Themes/ForestControls.axaml") });
        window.Styles.Add(new StyleInclude(baseUri) { Source = new Uri("avares://eViSTool.Desktop/Themes/Forest.axaml") });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static ServerRemoteViewModel Model(bool windows = true) => new(() => windows, _ => 0);

    private static T Named<T>(Window window, string automationId) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    private static IEnumerable<Button> Buttons(Window window, object? command) =>
        window.GetVisualDescendants().OfType<Button>().Where(b => ReferenceEquals(b.Command, command));

    [AvaloniaFact]
    public void Switched_off_shows_only_the_switch_and_the_warning()
    {
        var model = Model();
        model.ShowStatus(null);
        var window = Show(model);

        var status = Named<TextBlock>(window, "RemoteStatus");
        Assert.Equal(Loc.T("remote.statusOff"), status.Text);
        Assert.False(Named<CheckBox>(window, "RemoteEnabled").IsChecked);
        // адреса и код — только при включённом доступе
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<ComboBox>(), c => c.IsEffectivelyVisible);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), c => c.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Switching_on_shows_address_and_code()
    {
        var model = Model();
        var window = Show(model);

        Named<CheckBox>(window, "RemoteEnabled").IsChecked = true; // профиля нет — на диск и в агента ничего не уходит
        Dispatcher.UIThread.RunJobs();

        Assert.True(model.Enabled);
        Assert.True(Named<ComboBox>(window, "RemoteHost").IsEffectivelyVisible);
        var code = Named<TextBox>(window, "RemoteCode");
        Assert.True(code.IsEffectivelyVisible);
        Assert.True(code.IsReadOnly);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Firewall_button_is_shown_only_where_netsh_exists(bool windows)
    {
        var model = Model(windows);
        model.Enabled = true;
        var window = Show(model);

        var button = Assert.Single(Buttons(window, model.OpenFirewallCommand));
        Assert.Equal(windows, button.IsEffectivelyVisible);
        // остальные кнопки на месте при любой системе
        Assert.True(Assert.Single(Buttons(window, model.RegenerateKeyCommand)).IsEffectivelyVisible);
        Assert.True(Assert.Single(Buttons(window, model.CopyCodeCommand)).IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Status_turns_good_only_while_listening()
    {
        var model = Model();
        model.Enabled = true;
        var window = Show(model);
        var status = Named<TextBlock>(window, "RemoteStatus");

        model.ShowStatus(null);
        Assert.DoesNotContain("good", status.Classes);

        model.ShowStatus(new AgentStatus { RemotePort = 38417 });
        Assert.Contains("good", status.Classes);
        Assert.Equal(Loc.T("remote.statusListening", 38417), status.Text);

        model.ShowStatus(new AgentStatus { RemoteError = "порт занят" });
        Assert.DoesNotContain("good", status.Classes);
        window.Close();
    }

    [AvaloniaFact]
    public void Message_is_shown_only_when_there_is_one()
    {
        var model = Model();
        model.Enabled = true;
        var window = Show(model);
        var message = Named<TextBlock>(window, "RemoteMessage");
        Assert.False(message.IsEffectivelyVisible);

        model.Message = Loc.T("remote.copied");
        Dispatcher.UIThread.RunJobs();
        Assert.True(message.IsEffectivelyVisible);
        Assert.Equal(Loc.T("remote.copied"), message.Text);

        model.Message = "";
        Dispatcher.UIThread.RunJobs();
        Assert.False(message.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Reveal_button_follows_the_model()
    {
        var model = Model();
        model.Enabled = true;
        var window = Show(model);
        var reveal = Named<Button>(window, "RemoteReveal");
        Assert.Equal(Loc.T("remote.show"), reveal.Content);

        reveal.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Loc.T("remote.hide"), reveal.Content);

        model.Hide(); // ушли с вкладки или вышло время
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Loc.T("remote.show"), reveal.Content);
        window.Close();
    }

    [AvaloniaFact]
    public void Host_box_is_editable_and_two_way()
    {
        var model = Model();
        model.Enabled = true;
        model.Host = "192.168.1.42";
        var window = Show(model);
        var host = Named<ComboBox>(window, "RemoteHost");

        Assert.True(host.IsEditable);
        Assert.Equal("192.168.1.42", host.Text);

        host.Focus(); // фокус уходит во внутреннее поле: печатаем свой адрес
        Dispatcher.UIThread.RunJobs();
        host.GetVisualDescendants().OfType<TextBox>().First().SelectAll();
        window.KeyTextInput("my.host.example");
        Assert.Equal("my.host.example", model.Host); // каждый символ — сразу в модель

        model.Host = "10.0.0.7";
        Assert.Equal("10.0.0.7", host.Text);
        window.Close();
    }
}

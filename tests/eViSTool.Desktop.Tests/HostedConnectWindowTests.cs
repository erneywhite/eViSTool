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
using eViSTool.Core.Profiles;
using eViSTool.Desktop.Controls;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Окно «Сервер у хостера» с настоящей разметкой и лесной темой: ссылка в поле адреса раскладывается по полям,
/// «Подключить» отвечает подключением с зашифрованным паролем, без хоста кнопка недоступна. И карточка «Подключить по
/// FTP» в окне нового профиля — только у серверного. Проверка подключения в этих тестах не запускается.
/// </summary>
public sealed class HostedConnectWindowTests : IDisposable
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

    public HostedConnectWindowTests()
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

    private static HostedConnectWindow NewWindow() => new()
    {
        DataContext = new HostedConnectViewModel(null, "Подключить",
            (_, _, _) => throw new Xunit.Sdk.XunitException("проверка подключения в этом тесте не ожидалась")),
    };

    /// <summary>Открыть окно модально, как программа, и пока оно ждёт ответа — выполнить действие над ним.</summary>
    private static HostedConnection? Ask(HostedConnectWindow window, Action<HostedConnectWindow> act)
    {
        Dispatcher.UIThread.Post(() =>
        {
            window.UpdateLayout();
            act(window);
        }, DispatcherPriority.Background);
        return window.ShowModal<HostedConnection>();
    }

    private static T Named<T>(Visual root, string automationId) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    [AvaloniaFact]
    public void TypedLink_FillsTheFields_AndConnectAnswersWithTheEncryptedPassword()
    {
        var answer = Ask(NewWindow(), window =>
        {
            Named<TextBox>(window, "HostedAddress").Text = "ftp://gp_user:made-up-password@203.0.113.40:58021";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("58021", Named<TextBox>(window, "HostedPort").Text);
            Assert.Equal("gp_user", Named<TextBox>(window, "HostedUser").Text);
            Assert.Equal("made-up-password", Named<SecretTextBox>(window, "HostedPassword").Text);
            Mouse.Click(window, Named<Button>(window, "HostedOk"));
        });

        Assert.NotNull(answer);
        Assert.Equal("203.0.113.40", answer.Host);
        Assert.Equal(58021, answer.Port);
        Assert.Equal("gp_user", answer.User);
        Assert.NotEqual("made-up-password", answer.Password); // в подключение уходит только зашифрованный
        Assert.Equal("made-up-password", HostedSecret.Unprotect(answer.Password));
    }

    [AvaloniaFact]
    public void WithoutAHost_ConnectIsUnavailable()
    {
        var window = NewWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(Named<Button>(window, "HostedOk").IsEnabled);
        Named<TextBox>(window, "HostedAddress").Text = "203.0.113.40";
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<Button>(window, "HostedOk").IsEnabled);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(ProfileKind.Server, true)]
    [InlineData(ProfileKind.Client, false)]
    public void NewProfile_ConnectByFtpCard_IsOnlyForServerProfiles(ProfileKind kind, bool visible)
    {
        var window = new NewProfileWindow
        {
            DataContext = new NewProfileViewModel(kind, "Профиль", Path.Combine(Path.GetTempPath(), "evis-np-none")),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(visible, Named<Button>(window, "NewProfileConnectHosted").IsEffectivelyVisible);
        window.Close();
    }
}

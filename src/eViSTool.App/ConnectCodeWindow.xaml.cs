using System.Net.Http;
using System.Windows;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.App;

/// <summary>
/// Вставить код подключения к серверу на другом компьютере и проверить связь. Код не показывается — поле скрытое;
/// видно только, куда он ведёт (адрес и порт), чтобы не перепутать серверы.
/// </summary>
public partial class ConnectCodeWindow : Window
{
    private ConnectionCode? _code;
    private int _check;

    /// <summary>Разобранный код (после «ОК»).</summary>
    public ConnectionCode? Code { get; private set; }

    public ConnectCodeWindow(string okText)
    {
        InitializeComponent();
        OkButton.Content = okText;
        Loaded += (_, _) => CodeBox.Focus();
    }

    private void CodeBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _check++;
        CheckText.Text = "";
        var text = CodeBox.Password.Trim();
        _code = ConnectionCode.TryParse(text, out var code) ? code : null;
        ParsedText.Text = text.Length == 0 ? "" : _code is { } c ? Loc.T("connect.target", c.Host, c.Port) : Loc.T("connect.bad");
        ParsedText.Foreground = (System.Windows.Media.Brush)FindResource(_code is null && text.Length > 0 ? "Forest.Danger" : "Forest.Muted");
        CheckButton.IsEnabled = OkButton.IsEnabled = _code is not null;
    }

    /// <summary>Код из буфера — в скрытое поле, не показывая его.</summary>
    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText()) CodeBox.Password = Clipboard.GetText().Trim();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // буфер занят другой программой — можно вставить Ctrl+V
        }
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_code is not { } code) return;
        var check = ++_check;
        CheckButton.IsEnabled = false;
        CheckText.Foreground = (System.Windows.Media.Brush)FindResource("Forest.Muted");
        CheckText.Text = Loc.T("connect.checking");
        string result;
        var ok = false;
        try
        {
            using var client = AgentClient.ForRemote(code);
            var status = await client.StatusAsync();
            result = Loc.T("connect.ok", Loc.T(status.State switch
            {
                ServerState.Running => "server.stateRunning",
                ServerState.Starting => "server.stateStarting",
                ServerState.Stopping => "server.stateStopping",
                _ => "server.stateStopped",
            }), status.GameVersion ?? "?");
            ok = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            result = RemoteSecret.Describe(ex);
        }
        if (check != _check) return; // код успели поменять — ответ уже не про него
        CheckText.Text = result;
        CheckText.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "Forest.Good" : "Forest.Danger");
        CheckButton.IsEnabled = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_code is null) return;
        Code = _code;
        DialogResult = true;
    }
}

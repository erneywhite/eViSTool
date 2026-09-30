using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.App.ViewModels;

/// <summary>
/// Вкладка «Удалённый доступ» раздела «Сервер»: разрешить управление сервером с другой машины и выдать код подключения.
/// Код — секрет (в нём ключ), поэтому по умолчанию он закрыт точками: «Скопировать» кладёт его в буфер, не показывая,
/// «Показать» раскрывает на несколько секунд.
/// </summary>
public sealed partial class ServerRemoteViewModel : ObservableObject
{
    private static readonly TimeSpan RevealFor = TimeSpan.FromSeconds(15);

    private GameProfile? _profile;
    private bool _loading;
    private string _fingerprint = "";
    private RemoteSettings _settings = new();
    // код открыт — через RevealFor закрывается сам; кнопка «Скрыть» при этом работает всегда
    private readonly System.Windows.Threading.DispatcherTimer _hideTimer = new() { Interval = RevealFor };

    public ServerRemoteViewModel() => _hideTimer.Tick += (_, _) => Hide();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isListening;
    [ObservableProperty] private string _message = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodeText), nameof(RevealText))]
    private bool _revealed;

    public IReadOnlyList<string> Addresses { get; private set; } = [];
    public int Port => _settings.Port;
    public bool HasProfile => _profile is not null;

    public string CodeText => !Enabled || Code is not { } code ? "" : Revealed ? code : new string('•', 40);
    public string RevealText => Loc.T(Revealed ? "remote.hide" : "remote.show");

    private string? Code => _settings.Port > 0 && _settings.Key.Length > 0 && _fingerprint.Length > 0 && Host.Trim().Length > 0
        ? new ConnectionCode(Host.Trim(), _settings.Port, _settings.Key, _fingerprint).ToString()
        : null;

    /// <summary>Сменился активный профиль (null — не серверный).</summary>
    public void OnProfileSwitched(GameProfile? profile)
    {
        _profile = profile;
        Hide();
        Message = "";
        _loading = true;
        try
        {
            _settings = profile is null ? new() : RemoteAccess.Load(profile.Id);
            Addresses = RemoteAccess.LocalAddresses();
            OnPropertyChanged(nameof(Addresses));
            Enabled = _settings.Enabled;
            Host = _settings.Host is { Length: > 0 } h ? h : Addresses.FirstOrDefault() ?? "";
            LoadFingerprint();
        }
        finally
        {
            _loading = false;
        }
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(CodeText));
        ShowStatus(null);
    }

    private void LoadFingerprint()
    {
        _fingerprint = "";
        if (_profile is null || !File.Exists(RemoteAccess.CertFile(_profile.Id))) return;
        try
        {
            using var cert = RemoteAccess.EnsureCertificate(_profile.Id);
            _fingerprint = RemoteAccess.Fingerprint(cert);
        }
        catch (CryptographicException ex)
        {
            Message = ex.Message;
        }
    }

    /// <summary>Свежий статус агента (null — агента нет).</summary>
    public void ShowStatus(AgentStatus? status)
    {
        IsListening = Enabled && status?.RemotePort is not null;
        StatusText = !Enabled ? Loc.T("remote.statusOff")
            : status is null ? Loc.T("remote.statusNoAgent")
            : status.RemotePort is { } port ? Loc.T("remote.statusListening", port)
            : status.RemoteError is { Length: > 0 } error ? Loc.T("remote.statusError", error)
            : Loc.T("remote.statusStarting");
    }

    partial void OnEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(CodeText));
        if (_loading || _profile is not { } profile) return;
        try
        {
            _settings = value ? RemoteAccess.Enable(profile.Id) : RemoteAccess.Disable(profile.Id);
            if (value)
            {
                if (_settings.Host is null) SaveHost();
                LoadFingerprint();
                // агент нужен, чтобы слушать сеть: запускаем его (сам сервер — нет)
                _ = StartAgentAsync(profile);
            }
            Message = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException)
        {
            Message = Loc.T("remote.failedEnable", ex.Message);
        }
        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(CodeText));
        ShowStatus(null);
    }

    private async Task StartAgentAsync(GameProfile profile)
    {
        try
        {
            using var client = await AgentLauncher.EnsureRunningAsync(profile, startServer: false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or HttpRequestException)
        {
            Message = ex.Message;
        }
    }

    partial void OnHostChanged(string value)
    {
        OnPropertyChanged(nameof(CodeText));
        if (!_loading) SaveHost();
    }

    private void SaveHost()
    {
        if (_profile is null) return;
        try
        {
            _settings = RemoteAccess.Load(_profile.Id) with { Host = Host.Trim() };
            RemoteAccess.Save(_profile.Id, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = ex.Message;
        }
    }

    /// <summary>Показать код на несколько секунд (потом он закрывается сам) или сразу закрыть.</summary>
    [RelayCommand]
    private void ToggleReveal()
    {
        if (Revealed)
        {
            Hide();
            return;
        }
        Revealed = true;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    /// <summary>Закрыть код точками (и при уходе с вкладки).</summary>
    public void Hide()
    {
        _hideTimer.Stop();
        Revealed = false;
    }

    [RelayCommand]
    private void CopyCode()
    {
        if (Code is not { } code) return;
        try
        {
            Clipboard.SetText(code);
            Message = Loc.T("remote.copied");
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    private void RegenerateKey()
    {
        if (_profile is null) return;
        if (MessageBox.Show(Application.Current.MainWindow!, Loc.T("remote.newKeyAsk"), "eViSTool",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _settings = RemoteAccess.RegenerateKey(_profile.Id);
        Hide();
        Message = Loc.T("remote.newKeyDone");
        OnPropertyChanged(nameof(CodeText));
    }

    /// <summary>
    /// Разрешить входящие подключения на порт агента в брандмауэре Windows. Нужны права администратора — Windows спросит сама.
    /// </summary>
    [RelayCommand]
    private void OpenFirewall()
    {
        if (_settings.Port <= 0) return;
        var rule = $"eViSTool agent {_settings.Port}";
        var psi = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c netsh advfirewall firewall delete rule name=\"{rule}\" >nul & " +
                        $"netsh advfirewall firewall add rule name=\"{rule}\" dir=in action=allow protocol=TCP localport={_settings.Port}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
            Message = p is { HasExited: true, ExitCode: 0 } ? Loc.T("remote.firewallDone", _settings.Port) : Loc.T("remote.firewallFailed");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Message = Loc.T("remote.firewallCancelled"); // отказались в окне Windows
        }
    }

    public void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(RevealText));
        ShowStatus(null);
    }
}

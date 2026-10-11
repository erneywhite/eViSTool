using System.ComponentModel;
using System.Diagnostics;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server.Remote;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.App.Tests;

/// <summary>
/// Вкладка «Удалённый доступ»: правило брандмауэра есть только в Windows, а код подключения (в нём ключ) закрыт точками
/// и в сообщения не попадает. Платформа подменена: буфер обмена и таймер ненастоящие, netsh не запускается.
/// </summary>
public sealed class ServerRemoteViewModelTests : IDisposable
{
    private const int Port = 34567;
    private const string Host = "192.0.2.10"; // TEST-NET: адрес выдуманный

    private readonly IUiPlatform? _before = Ui.IsSet ? Ui.Platform : null;
    private readonly FakePlatform _platform = new();
    private readonly List<RemoteProfile> _profiles = [];

    public ServerRemoteViewModelTests() => Ui.Platform = _platform;

    public void Dispose()
    {
        foreach (var p in _profiles) p.Dispose();
        if (_before is not null) Ui.Platform = _before;
    }

    /// <summary>Профиль с включённым удалённым доступом (файлы — в папке данных тестов, настоящие данные не трогаются).</summary>
    private sealed class RemoteProfile : IDisposable
    {
        public GameProfile Profile { get; } =
            new() { Id = "remote-vm-" + Guid.NewGuid().ToString("N")[..8], Name = "Сервер", Kind = ProfileKind.Server };

        public string Key { get; } = RemoteAccess.NewKey();
        public string Fingerprint { get; }

        public RemoteProfile()
        {
            RemoteAccess.Save(Profile.Id, new RemoteSettings { Enabled = true, Port = Port, Key = Key, Host = Host });
            using var cert = RemoteAccess.EnsureCertificate(Profile.Id);
            Fingerprint = RemoteAccess.Fingerprint(cert);
        }

        public string Code => new ConnectionCode(Host, Port, Key, Fingerprint).ToString();

        public void Dispose()
        {
            File.Delete(RemoteAccess.FileFor(Profile.Id));
            File.Delete(RemoteAccess.CertFile(Profile.Id));
        }
    }

    private RemoteProfile NewProfile()
    {
        var p = new RemoteProfile();
        _profiles.Add(p);
        return p;
    }

    /// <summary>Модель на «системе» с заданным признаком Windows; запуски netsh складывает в <paramref name="runs"/>.</summary>
    private static ServerRemoteViewModel Model(bool windows, List<ProcessStartInfo> runs, Func<ProcessStartInfo, int?>? result = null) =>
        new(() => windows, psi =>
        {
            runs.Add(psi);
            return result is null ? 0 : result(psi);
        });

    // ---- брандмауэр: только Windows

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CanOpenFirewall_FollowsTheSystem(bool windows) =>
        Assert.Equal(windows, Model(windows, []).CanOpenFirewall);

    [Fact]
    public void CanOpenFirewall_ByDefault_IsTrueOnlyOnWindows() =>
        Assert.Equal(OperatingSystem.IsWindows(), new ServerRemoteViewModel().CanOpenFirewall);

    [Fact]
    public void OpenFirewall_OnLinux_RunsNothing()
    {
        var profile = NewProfile();
        var runs = new List<ProcessStartInfo>();
        var vm = Model(windows: false, runs);
        vm.OnProfileSwitched(profile.Profile);
        Assert.Equal(Port, vm.Port); // порт известен — команда не запускается именно из-за системы

        vm.OpenFirewallCommand.Execute(null);

        Assert.Empty(runs);
        Assert.Equal("", vm.Message);
    }

    [Fact]
    public void OpenFirewall_OnWindows_AsksForPortWithAdminRights()
    {
        var profile = NewProfile();
        var runs = new List<ProcessStartInfo>();
        var vm = Model(windows: true, runs);
        vm.OnProfileSwitched(profile.Profile);

        vm.OpenFirewallCommand.Execute(null);

        var psi = Assert.Single(runs);
        Assert.Equal("runas", psi.Verb); // права администратора спрашивает сама Windows
        Assert.Contains($"localport={Port}", psi.Arguments);
        Assert.Contains($"name=\"eViSTool agent {Port}\"", psi.Arguments);
        Assert.Equal(Loc.T("remote.firewallDone", Port), vm.Message);
    }

    [Theory]
    [InlineData(1)]      // netsh не справился
    [InlineData(null)]   // не дождались ответа
    public void OpenFirewall_NotAdded_SaysSo(int? exitCode)
    {
        var profile = NewProfile();
        var vm = Model(windows: true, [], _ => exitCode);
        vm.OnProfileSwitched(profile.Profile);

        vm.OpenFirewallCommand.Execute(null);

        Assert.Equal(Loc.T("remote.firewallFailed"), vm.Message);
    }

    [Fact]
    public void OpenFirewall_DeclinedInWindowsDialog_SaysCancelled()
    {
        var profile = NewProfile();
        var vm = new ServerRemoteViewModel(() => true, _ => throw new Win32Exception(1223));
        vm.OnProfileSwitched(profile.Profile);

        vm.OpenFirewallCommand.Execute(null);

        Assert.Equal(Loc.T("remote.firewallCancelled"), vm.Message);
    }

    [Fact]
    public void OpenFirewall_WithoutPort_RunsNothing()
    {
        var runs = new List<ProcessStartInfo>();
        var vm = Model(windows: true, runs);
        vm.OnProfileSwitched(null); // не серверный профиль: порта нет

        vm.OpenFirewallCommand.Execute(null);

        Assert.Empty(runs);
    }

    // ---- код подключения: секрет

    [Fact]
    public void Code_IsMasked_UntilRevealed_AndClosesByTimer()
    {
        var profile = NewProfile();
        var vm = Model(windows: true, []);
        vm.OnProfileSwitched(profile.Profile);

        Assert.Equal(new string('•', 40), vm.CodeText);
        Assert.DoesNotContain(profile.Key, vm.CodeText);
        Assert.Equal(Loc.T("remote.show"), vm.RevealText);

        vm.ToggleRevealCommand.Execute(null);
        Assert.Equal(profile.Code, vm.CodeText);
        Assert.Equal(Loc.T("remote.hide"), vm.RevealText);
        Assert.True(_platform.Timer.IsEnabled);

        _platform.Timer.Fire(); // прошло время — код закрылся сам
        Assert.Equal(new string('•', 40), vm.CodeText);
        Assert.Equal(Loc.T("remote.show"), vm.RevealText);

        vm.ToggleRevealCommand.Execute(null);
        vm.ToggleRevealCommand.Execute(null); // «Скрыть» закрывает сразу
        Assert.Equal(new string('•', 40), vm.CodeText);
        Assert.False(_platform.Timer.IsEnabled);
    }

    [Fact]
    public void Code_IsHidden_WhenRemoteAccessIsOff()
    {
        var profile = NewProfile();
        var vm = Model(windows: true, []);
        vm.OnProfileSwitched(profile.Profile);
        vm.ToggleRevealCommand.Execute(null);

        vm.Enabled = false; // выключили — кода нет совсем, даже открытого

        Assert.Equal("", vm.CodeText);
    }

    [Fact]
    public void CopyCode_PutsItIntoClipboard_WithoutShowingOrNamingIt()
    {
        var profile = NewProfile();
        var vm = Model(windows: true, []);
        vm.OnProfileSwitched(profile.Profile);

        vm.CopyCodeCommand.Execute(null);

        Assert.Equal(profile.Code, _platform.Clipboard);
        Assert.Equal(Loc.T("remote.copied"), vm.Message); // о коде — ни слова, только «скопирован»
        Assert.DoesNotContain(profile.Key, vm.Message);
        Assert.Equal(new string('•', 40), vm.CodeText);    // копирование код на экране не открывает
    }

    /// <summary>Платформа теста: буфер обмена и таймер ненастоящие, остальное тут не нужно.</summary>
    private sealed class FakePlatform : IUiPlatform
    {
        public ManualTimer Timer { get; } = new();
        public string? Clipboard { get; private set; }

        public void SetClipboard(string text) => Clipboard = text;
        public IUiTimer CreateTimer(TimeSpan interval) => Timer;

        public Answer Ask(string text, AskButtons buttons, Answer? byDefault = null, bool warning = false) => throw new InvalidOperationException(text);
        public void Notify(string text, NoticeKind kind) => throw new InvalidOperationException(text);
        public bool IsUiThread => true;
        public void Post(Action action) => action();
        public void PostIdle(Action action) => action();
        public void Invoke(Action action) => action();
        public IReadOnlyList<string>? PickFiles(string title, IReadOnlyList<FileFilter> filters, bool multiple) => null;
        public string? PickSaveFile(string title, IReadOnlyList<FileFilter> filters, string suggestedName, string? folder = null, bool confirmOverwrite = true) => null;
        public string? PickFolder(string title, string? initialFolder) => null;
        public void OpenUrl(string url) { }
        public void OpenFolder(string dir) { }
        public void ShowInFolder(string path) { }
        public void MoveToTrash(string path) { }
        public void OpenTrash() { }
        public bool IsMainWindowVisible => false;
        public void RestartAfterClose(string exe) { }
        public string WriteCrashLog(Exception ex) => "";
    }

    private sealed class ManualTimer : IUiTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsEnabled { get; private set; }
        public event EventHandler? Tick;
        public void Start() => IsEnabled = true;
        public void Stop() => IsEnabled = false;

        /// <summary>Прошёл интервал: таймер сработал (если запущен).</summary>
        public void Fire()
        {
            if (IsEnabled) Tick?.Invoke(this, EventArgs.Empty);
        }
    }
}

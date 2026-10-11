using System.Windows;
using eViSTool.App.ViewModels;
using eViSTool.Core.Diagnostics;
using eViSTool.Core.Notifications;
using eViSTool.Core.Packs;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.App;

/// <summary>Окна программы (<see cref="AppWindows"/>) на WPF: владелец — главное окно, если оно на экране.</summary>
public sealed class WpfAppWindows : IAppWindows
{
    /// <summary>Владелец новых окон: главное окно, если оно на экране (иначе окно без владельца).</summary>
    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } main ? main : null;

    public GameProfile? ClientProfile(GameProfile source, bool clone, string suggestedName)
    {
        var dlg = new ClientProfileWindow(source, clone, suggestedName) { Owner = Owner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    public GameProfile? CloneServerProfile(GameProfile source, bool serverRunning, string? importName = null)
    {
        var dlg = new CloneProfileWindow(source, serverRunning, importName) { Owner = Owner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    public NewProfileChoice? NewProfile(ProfileKind kind, string suggestedName)
    {
        var dlg = new NewProfileWindow(kind, suggestedName) { Owner = Owner };
        return dlg.ShowDialog() == true
            ? new NewProfileChoice(dlg.ProfileName, dlg.DataDir, dlg.Existing, dlg.RemoteCode, dlg.ExistingDir)
            : null;
    }

    public ConnectionCode? AskConnectionCode(string okText)
    {
        var dlg = new ConnectCodeWindow(okText) { Owner = Owner };
        return dlg.ShowDialog() == true ? dlg.Code : null;
    }

    public void ShowCleanup(GameProfile profile)
    {
        if (Application.Current?.MainWindow is not { } owner) return;
        CleanupWindow.Open(owner, profile);
    }

    public void ShowFindServer(FindServerViewModel model) => new FindServerWindow(model) { Owner = Owner }.Show();

    public VersionOption? PickVersion(string heading, IReadOnlyList<VersionOption> options)
    {
        var dlg = new RollbackWindow(heading, options) { Owner = Owner };
        return dlg.ShowDialog() == true ? dlg.Selected : null;
    }

    public PackExportOptions? ExportPack(string defaultName, int enabledCount, int disabledCount, bool hasModConfig)
    {
        var dlg = new ExportPackWindow(defaultName, enabledCount, disabledCount, hasModConfig) { Owner = Owner };
        return dlg.ShowDialog() == true ? dlg.Options : null;
    }

    public PackImportChoice? ConfirmPackImport(PackImportPlan plan)
    {
        var dlg = new ImportPackWindow(plan) { Owner = Owner };
        return dlg.ShowDialog() == true ? new PackImportChoice(dlg.Mirror, dlg.ApplyConfig) : null;
    }

    public AlsoInstallAnswer? AskAlsoInstall(string hint, string currentName, string currentKind, string currentDetail, IReadOnlyList<AlsoChoice> others)
    {
        var dlg = new AlsoInstallWindow(hint, currentName, currentKind, currentDetail, others) { Owner = Owner };
        return dlg.ShowDialog() == true ? new AlsoInstallAnswer(dlg.Chosen, dlg.RememberChoice) : null;
    }

    public void ShowScreenshots(IReadOnlyList<string> urls, int index, string title) =>
        new ScreenshotWindow(urls, index, title) { Owner = Owner }.ShowDialog();

    public BanChoice? Ban(string player)
    {
        var dlg = new BanWindow(player) { Owner = Owner };
        return dlg.ShowDialog() == true ? new BanChoice(dlg.Amount, dlg.Unit, dlg.Reason) : null;
    }

    public RoleCopyChoice? DuplicateRole(string sourceCode, string sourceName, IReadOnlyList<string> takenCodes)
    {
        var dlg = new DuplicateRoleWindow(sourceCode, sourceName, takenCodes) { Owner = Owner };
        return dlg.ShowDialog() == true ? new RoleCopyChoice(dlg.RoleCode, dlg.RoleName) : null;
    }

    public NotifyChannel? EditNotifyChannel(NotifyChannel channel, bool isNew)
    {
        var dlg = new NotifyChannelWindow(channel, isNew) { Owner = Owner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    public string? PickRemoteFolder(IServerData data, string? start, string heading)
    {
        var picker = new RemoteFolderWindow(data, start, heading) { Owner = Owner };
        return picker.ShowDialog() == true ? picker.Selected : null;
    }

    public ServerCommand? PickServerCommand(ServerViewModel server)
    {
        var dlg = new ServerCommandsWindow(server) { Owner = Owner };
        return dlg.ShowDialog() == true ? dlg.Chosen : null;
    }

    public void ShowModErrors(MainViewModel main, GameProfile profile, ModErrorReport report) =>
        new ModErrorsWindow(main, profile, report) { Owner = Owner }.Show();

    public void ShowUpdatePrompt(AboutViewModel about, Action skip) =>
        new UpdateWindow(about, skip) { Owner = Owner }.Show(); // не модально: программой можно пользоваться, пока окно открыто

    public void ShowGameCrash(MainViewModel main, GameProfile profile, CrashFinding finding)
    {
        var window = CrashWindow.ForGame(main, profile, finding);
        window.Show(); // не модально: игра (если мир закрылся, она открыта) и программа остаются доступны
        window.Activate();
    }

    public void ShowServerCrash(MainViewModel main, GameProfile profile, ServerCrashInfo crash)
    {
        var window = CrashWindow.ForServer(main, profile, crash);
        window.Show();
        window.Activate();
    }
}

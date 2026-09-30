using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core;
using eViSTool.Core.AppUpdate;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Versioning;

namespace eViSTool.App.ViewModels;

/// <summary>Вкладка «О программе»: версия, ссылки, поддержка автора, обновление программы из релизов GitHub.</summary>
public sealed partial class AboutViewModel : ObservableObject
{
    public const string KofiUrl = "https://ko-fi.com/erneywhite";

    private readonly AppUpdater _updater = new();

    /// <summary>Профили — чтобы перед перезапуском после обновления остановить агентов без работающего сервера.</summary>
    public Func<IEnumerable<GameProfile>>? Profiles { get; set; }

    public string AppVersion { get; } =
        typeof(AboutViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "?";

    [ObservableProperty] private string _updateStatus = "";

    /// <summary>Найденный новый релиз (null — не искали или обновлений нет).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateButtonText), nameof(UpdateBadgeText))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    private AppRelease? _availableUpdate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand), nameof(CheckAppUpdateCommand))]
    private bool _isUpdating;

    public bool HasUpdate => AvailableUpdate is not null;
    public string UpdateButtonText => AvailableUpdate is { } r ? Loc.T("about.updateInstall", r.Version) : "";
    public string UpdateBadgeText => AvailableUpdate is { } r ? Loc.T("shell.updateAvailable", r.Version) : "";

    [RelayCommand]
    private static void OpenKofi() => Shell.OpenUrl(KofiUrl);

    [RelayCommand]
    private static void OpenUrl(string? url)
    {
        if (!string.IsNullOrEmpty(url)) Shell.OpenUrl(url);
    }

    [RelayCommand]
    private void OpenReleaseNotes()
    {
        if (AvailableUpdate?.PageUrl is { Length: > 0 } url) Shell.OpenUrl(url);
    }

    /// <summary>Тихая проверка при запуске: ошибки (нет сети, GitHub недоступен) не показываем.</summary>
    public async Task CheckQuietlyAsync()
    {
        try
        {
            if (ModVersion.ParseOrNull(AppVersion) is { } current) AvailableUpdate = await _updater.FindUpdateAsync(current);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Newtonsoft.Json.JsonException)
        {
        }
    }

    private bool CanCheck => !IsUpdating;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAppUpdate()
    {
        if (ModVersion.ParseOrNull(AppVersion) is not { } current) return;
        UpdateStatus = Loc.T("about.updateChecking");
        try
        {
            AvailableUpdate = await _updater.FindUpdateAsync(current);
            UpdateStatus = AvailableUpdate is { } r ? Loc.T("about.updateAvailable", r.Version) : Loc.T("about.updateNone");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Newtonsoft.Json.JsonException)
        {
            UpdateStatus = Loc.T("about.updateCheckFailed", ex.Message);
        }
    }

    private bool CanInstall => AvailableUpdate is not null && !IsUpdating;

    /// <summary>Скачать, сверить, поставить и предложить перезапуск. Работающий сервер не трогаем.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallUpdate()
    {
        if (AvailableUpdate is not { } release) return;
        IsUpdating = true;
        try
        {
            var progress = new Progress<double>(p => UpdateStatus = Loc.T("about.updateDownloading", (int)(p * 100)));
            var zip = await _updater.DownloadAsync(release, AppPaths.Downloads, progress);
            UpdateStatus = Loc.T("about.updateInstalling");
            var appDir = AppContext.BaseDirectory;
            await Task.Run(() => AppUpdater.Install(zip, appDir));
            try { File.Delete(zip); } catch (IOException) { }

            AvailableUpdate = null;
            UpdateStatus = Loc.T("about.updateInstalled", release.Version);
            // агенты без работающего сервера — на выход: новое окно поднимет агентов уже новой версии
            if (Profiles is { } profiles) await Core.Server.AgentLauncher.StopIdleAgentsAsync([.. profiles()]);
            var owner = Application.Current.MainWindow!;
            if (MessageBox.Show(owner, Loc.T("about.updateRestartAsk", release.Version), "eViSTool",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes)
                App.RestartAfterClose(Path.Combine(appDir, AppUpdater.MainExe));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or InvalidDataException)
        {
            UpdateStatus = Loc.T("about.updateFailed", ex.Message);
        }
        finally
        {
            IsUpdating = false;
        }
    }
}

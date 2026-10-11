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

    /// <summary>Все версии новее этой, от новых к старым (первая — <see cref="AvailableUpdate"/>): что изменилось за пропущенные.</summary>
    public IReadOnlyList<AppRelease> MissedReleases { get; private set; } = [];

    /// <summary>Среди пропущенных версий есть рекомендуемая — обновиться настоятельно советуем.</summary>
    public bool IsRecommended => MissedReleases.Any(r => r.Recommended);

    /// <summary>Проверка нашла обновление (вызывается на потоке окна) — главное окно решает, показать ли «Вышла новая версия».</summary>
    public event Action? UpdateFound;

    /// <summary>Обновление поставило само это окно (кнопкой в «О программе») — про перезапуск оно спрашивает само.</summary>
    public bool UpdatedHere { get; private set; }
    public string UpdateButtonText => AvailableUpdate is { } r ? Loc.T("about.updateInstall", r.Version) : "";
    public string UpdateBadgeText => AvailableUpdate is { } r
        ? Loc.T(IsRecommended ? "shell.updateRecommended" : "shell.updateAvailable", r.Version)
        : "";

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
            if (ModVersion.ParseOrNull(AppVersion) is { } current) SetFound(await _updater.FindUpdatesAsync(current));
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
            SetFound(await _updater.FindUpdatesAsync(current));
            UpdateStatus = AvailableUpdate is { } r ? Loc.T("about.updateAvailable", r.Version) : Loc.T("about.updateNone");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Newtonsoft.Json.JsonException)
        {
            UpdateStatus = Loc.T("about.updateCheckFailed", ex.Message);
        }
    }

    private void SetFound(IReadOnlyList<AppRelease> releases)
    {
        MissedReleases = releases;
        OnPropertyChanged(nameof(IsRecommended)); // до AvailableUpdate: текст ссылки в боковой панели пересчитается уже с ним
        AvailableUpdate = releases.FirstOrDefault();
        if (AvailableUpdate is not null) UpdateFound?.Invoke();
    }

    private bool CanInstall => AvailableUpdate is not null && !IsUpdating;

    /// <summary>Скачать, сверить, поставить и предложить перезапуск. Работающий сервер не трогаем.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallUpdate()
    {
        if (await InstallAsync() is { } release
            && MessageBox.Show(Application.Current.MainWindow!, Loc.T("about.updateRestartAsk", release.Version), "eViSTool",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes)
            App.RestartAfterClose(Path.Combine(AppContext.BaseDirectory, AppUpdater.MainExe));
    }

    /// <summary>
    /// Скачать, сверить и поставить найденное обновление; перезапуск — за вызывающим. null — не поставилось
    /// (причина — в <see cref="UpdateStatus"/>) или ставить нечего.
    /// </summary>
    public async Task<AppRelease?> InstallAsync()
    {
        if (AvailableUpdate is not { } release || IsUpdating) return null;
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
            UpdatedHere = true; // сами и спросим про перезапуск — наблюдатель за exe не перебивает
            UpdateStatus = Loc.T("about.updateInstalled", release.Version);
            // агенты без работающего сервера — на выход: новое окно поднимет агентов уже новой версии
            if (Profiles is { } profiles) await Core.Server.AgentLauncher.StopIdleAgentsAsync([.. profiles()]);
            return release;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or InvalidDataException or TimeoutException)
        {
            UpdateStatus = Loc.T("about.updateFailed", ex.Message);
            return null;
        }
        finally
        {
            IsUpdating = false;
        }
    }
}

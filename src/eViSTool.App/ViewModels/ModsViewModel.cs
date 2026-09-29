using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Game;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Versioning;
using Microsoft.Win32;

namespace eViSTool.App.ViewModels;

public sealed partial class ModsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ModUpdateService _service;
    private readonly ModDbClient _db;
    private readonly ModUpdater _updater;

    // последний ответ модбазы: после локальных операций статусы не теряются
    private Dictionary<string, ModDbResult> _remote = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<LocalMod> _locals = [];

    public ObservableCollection<ModRowViewModel> Rows { get; } = [];
    public ICollectionView View { get; }

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _onlyIssues;
    [ObservableProperty] private string _search = "";

    /// <summary>Идёт скачивание/установка — кнопки операций недоступны.</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Проблемы с зависимостями включённых модов (считаются по диску, без сети).</summary>
    [ObservableProperty] private IReadOnlyList<DependencyIssue> _dependencyIssues = [];
    [ObservableProperty] private int _updateCount;

    public bool HasDependencyIssues => DependencyIssues.Count > 0;
    public string DependencyText => DependencyIssues.Count == 0 ? ""
        : $"Проблемы с зависимостями ({DependencyIssues.Count}): " + string.Join("; ", DependencyIssues.Select(i => i.Describe()));
    public string UpdateAllText => $"Обновить всё ({UpdateCount})";

    partial void OnDependencyIssuesChanged(IReadOnlyList<DependencyIssue> value)
    {
        OnPropertyChanged(nameof(HasDependencyIssues));
        OnPropertyChanged(nameof(DependencyText));
    }

    partial void OnUpdateCountChanged(int value) => OnPropertyChanged(nameof(UpdateAllText));

    public ModsViewModel(MainViewModel main, ModDbClient db)
    {
        _main = main;
        _db = db;
        _service = new ModUpdateService(db);
        _updater = new ModUpdater(db);
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is ModRowViewModel r
            && (!OnlyIssues || r.NeedsAttention)
            && (Search.Length == 0
                || r.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || r.ModId.Contains(Search, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnOnlyIssuesChanged(bool value) => View.Refresh();
    partial void OnSearchChanged(string value) => View.Refresh();

    private ResolvedProfile? Profile => _main.ActiveProfile?.Resolved;

    /// <summary>Строка под кнопками: какие папки и какая игра.</summary>
    public string ProfileInfo => Profile is { } r
        ? $"Игра: {r.GameVersion?.ToString() ?? "не найдена"}   ·   Папки модов: {string.Join("; ", r.ModDirs)}"
        : "Профиль не выбран";

    /// <summary>Сменился профиль или его настройки — перечитываем моды с диска, ответ модбазы про другой набор сбрасываем.</summary>
    public void OnProfileSwitched()
    {
        _remote = new(StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(ProfileInfo));
        ReloadLocal();
        StatusText = "Моды прочитаны с диска. «Проверить обновления» — сверить с модбазой.";
        if (_main.AutoCheckUpdates && Profile?.GameVersion is not null && CheckCommand.CanExecute(null))
            CheckCommand.Execute(null);
    }

    /// <summary>Перечитать папки модов (без сети) и перестроить таблицу с уже известными статусами.</summary>
    public void ReloadLocal()
    {
        if (_main.ActiveProfile is not { } profile) return;
        profile.Refresh(notify: false); // игра могла поменять свои настройки
        try
        {
            _locals = ModUpdateService.ScanLocal(profile.Resolved);
        }
        catch (IOException ex)
        {
            _locals = [];
            StatusText = $"Не удалось прочитать папку модов: {ex.Message}";
        }
        Rebuild();
        LocalModsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Rebuild()
    {
        // без версии игры с модбазой не сравнить — покажем только локальное
        var game = Profile?.GameVersion ?? ModVersion.ParseOrNull("0.0")!;
        var remote = Profile?.GameVersion is null ? new Dictionary<string, ModDbResult>() : _remote;
        var policy = _main.ActiveProfile?.Model.ToPolicy() ?? ModPolicy.Empty;
        var results = UpdateChecker.Evaluate(_locals, remote, game, _main.AllowUnstable, policy);
        UpdateCount = results.Count(r => r.Status == ModStatus.UpdateAvailable && r.LatestCompatible?.MainFile is not null);
        DependencyIssues = Dependencies.FindIssues(_locals);

        Rows.Clear();
        foreach (var r in results) Rows.Add(new ModRowViewModel(r));

        var disabled = results.Count(r => r.Local.IsDisabled);
        var parts = new List<string> { $"Модов: {results.Count}", $"выключено: {disabled}" };
        if (_remote.Count > 0)
        {
            parts.Add($"обновлений: {results.Count(r => r.Status == ModStatus.UpdateAvailable)}");
            parts.Add($"требуют внимания: {Rows.Count(r => r.NeedsAttention)}");
        }
        Summary = string.Join(" · ", parts);
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task CheckAsync(CancellationToken ct)
    {
        if (Profile is null) return;

        try
        {
            ReloadLocal();
            if (Profile?.GameVersion is not { } game)
            {
                StatusText = "Не определена версия игры — укажи папку игры в настройках профиля.";
                return;
            }
            var progress = new Progress<string>(s => StatusText = s);
            _remote = await _service.FetchRemoteAsync(_locals, progress, ct);
            Rebuild();
            StatusText = $"Проверено {DateTime.Now:HH:mm:ss} для игры {game}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Проверка отменена";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            StatusText = $"Ошибка: {ex.Message}";
        }
    }

    // ---------- включение / выключение ----------

    [RelayCommand]
    private void ToggleEnabled(ModRowViewModel? row)
    {
        if (row?.Local.Info is not { } info || Profile is not { } profile) return;

        if (GameProcess.IsRunning(profile.Profile))
        {
            // клиент при выходе перезаписывает свои настройки, сервер применит только после перезапуска
            var server = profile.Profile.Kind == ProfileKind.Server;
            var what = server
                ? "Сервер запущен. Изменение вступит в силу после перезапуска, а при остановке сервер может перезаписать свой конфиг."
                : "Игра запущена. При выходе она перезапишет свои настройки, и изменение потеряется — лучше сначала закрыть игру.";
            if (!Confirm($"{what}\n\nВсё равно {(row.IsEnabled ? "выключить" : "включить")} «{row.Name}»?"))
            {
                ReloadLocal(); // вернуть галочку как было
                return;
            }
        }

        try
        {
            ModConfigEditor.SetEnabled(profile, info, enabled: !row.IsEnabled);
            StatusText = $"«{row.Name}» {(row.IsEnabled ? "выключен" : "включён")}";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Error($"Не удалось изменить настройки игры:\n{ex.Message}");
        }
        ReloadLocal();
    }

    // ---------- удаление ----------

    [RelayCommand]
    private void Delete(ModRowViewModel? row)
    {
        if (row is null || Profile is not { } profile) return;

        var running = GameProcess.IsRunning(profile.Profile)
            ? "\n\nИгра/сервер сейчас запущены — файл может быть занят." : "";
        if (!Confirm($"Удалить «{row.Name}» {row.Installed}?\n\nФайл уйдёт в Корзину, его можно будет восстановить.{running}"))
            return;

        try
        {
            Shell.MoveToRecycleBin(row.Local.Path);
            // если других копий мода не осталось — чистим его и из списка выключенных
            if (row.Local.Info is { } info && _locals.Count(l => l.Info?.ModId == info.ModId) == 1)
                ModConfigEditor.Forget(profile, info);
            StatusText = $"«{row.Name}» удалён в Корзину";
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            Error($"Не удалось удалить «{row.Name}»:\n{ex.Message}");
        }
        ReloadLocal();
    }

    // ---------- добавление из архива ----------

    [RelayCommand]
    private void AddFromFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Выбери мод (zip)",
            Filter = "Моды Vintage Story (*.zip)|*.zip",
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true) AddFiles(dlg.FileNames);
    }

    /// <summary>Установка набора zip (кнопка или перетаскивание).</summary>
    public void AddFiles(IEnumerable<string> files)
    {
        if (Profile is not { } profile) return;

        var zips = files.Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        if (zips.Count == 0)
        {
            Error("Моды Vintage Story — это zip-архивы, а среди выбранных файлов их нет.");
            return;
        }

        if (GameProcess.IsRunning(profile.Profile)
            && !Confirm("Игра/сервер сейчас запущены: новые моды подхватятся только после перезапуска, а заменяемые файлы могут быть заняты.\n\nПродолжить?"))
            return;

        var installed = new List<string>();
        var problems = new List<string>();
        foreach (var zip in zips)
            InstallZip(profile, zip, interactive: true, installed, problems);

        ReloadLocal();
        if (installed.Count > 0) StatusText = "Установлено: " + string.Join("; ", installed);

        if (problems.Count > 0)
        {
            var text = (installed.Count > 0 ? "Установлено:\n• " + string.Join("\n• ", installed) + "\n\n" : "")
                       + "Внимание:\n• " + string.Join("\n• ", problems);
            MessageBox.Show(Application.Current.MainWindow, text, "eViSTool", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Ставит один zip в профиль: старая версия — в хранилище, выключенный мод остаётся выключенным.
    /// interactive — спрашивать про ту же версию и даунгрейд (при обновлении/откате не спрашиваем: решение уже принято).
    /// </summary>
    private bool InstallZip(ResolvedProfile profile, string zip, bool interactive, List<string> installed, List<string> problems)
    {
        try
        {
            var plan = ModInstaller.Plan(zip, profile, ModUpdateService.ScanLocal(profile));
            var info = plan.Incoming.Info!;
            var old = plan.Replaces.FirstOrDefault()?.Info?.Version;

            if (interactive && plan.IsSameVersion && !Confirm($"«{info.Name}» {info.Version} уже установлен.\n\nПереустановить?"))
                return false;
            if (interactive && plan.IsDowngrade && !Confirm($"Установлен «{info.Name}» {old}, а ставится более старая {info.Version}.\n\nОткатиться на {info.Version}?"))
                return false;

            var disabledSet = new HashSet<string>(profile.DisabledMods);
            var wasDisabled = plan.Replaces.Any(r => ModUpdateService.IsDisabled(r, disabledSet));
            ModInstaller.Apply(plan, ModBackupStore.ForProfile(profile.Profile));
            // выключенный мод остаётся выключенным и в новой версии
            if (wasDisabled) ModConfigEditor.SetEnabled(profile, info, enabled: false);

            installed.Add(plan.IsReplace ? $"{info.Name}: {old} → {info.Version}" : $"{info.Name} {info.Version}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            problems.Add($"{Path.GetFileName(zip)}: {ex.Message}");
            return false;
        }
    }

    private void Report(string title, List<string> installed, List<string> problems)
    {
        ReloadLocal();
        _ = FetchNewModsAsync();
        // зависимости — по итоговому состоянию, а не на момент установки каждого мода
        if (installed.Count > 0)
            problems.AddRange(DependencyIssues.Select(i => "зависимость " + i.Describe()));
        StatusText = installed.Count > 0 ? $"{title}: " + string.Join("; ", installed) : $"{title}: ничего не изменилось";
        if (problems.Count == 0) return;
        var text = (installed.Count > 0 ? $"{title}:\n• " + string.Join("\n• ", installed) + "\n\n" : "")
                   + "Внимание:\n• " + string.Join("\n• ", problems);
        MessageBox.Show(Application.Current.MainWindow, text, "eViSTool", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static bool ConfirmIfRunning(ResolvedProfile profile) =>
        !GameProcess.IsRunning(profile.Profile)
        || Confirm("Игра/сервер сейчас запущены: изменения подхватятся только после перезапуска, а заменяемые файлы могут быть заняты.\n\nПродолжить?");

    /// <summary>Скачать релиз и поставить его. Ошибки — в problems.</summary>
    private async Task<bool> DownloadAndInstallAsync(ResolvedProfile profile, string modId, string name, ModDbRelease release,
        List<string> installed, List<string> problems)
    {
        string? file = null;
        try
        {
            var progress = new Progress<double>(x => StatusText = $"Скачивание {name} {release.ModVersion}… {x:P0}");
            file = await _updater.DownloadReleaseAsync(release, modId, progress);
            return InstallZip(profile, file, interactive: false, installed, problems);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            problems.Add($"{name}: не удалось скачать {release.ModVersion} — {ex.Message}");
            return false;
        }
        finally
        {
            if (file is not null) _updater.Cleanup(file);
        }
    }

    /// <summary>Если модбазу уже опрашивали — досверить только что появившиеся моды, чтобы не висело «Не проверялся».</summary>
    private async Task FetchNewModsAsync()
    {
        if (_remote.Count == 0) return;
        var fresh = _locals.Where(l => l.Info is not null && !_remote.ContainsKey(l.Info.ModId)).ToList();
        if (fresh.Count == 0) return;
        try
        {
            foreach (var (id, r) in await _service.FetchRemoteAsync(fresh))
                _remote[id] = r;
            Rebuild();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // не критично: статус останется «Не проверялся» до следующей проверки
        }
    }

    // ---------- обновление ----------

    [RelayCommand]
    private async Task UpdateOneAsync(ModRowViewModel? row)
    {
        if (row is not { CanUpdate: true } || Profile is not { } profile || IsBusy) return;
        if (!ConfirmIfRunning(profile)) return;

        var installed = new List<string>();
        var problems = new List<string>();
        IsBusy = true;
        try
        {
            await DownloadAndInstallAsync(profile, row.ModId, row.Name, row.Result.LatestCompatible!, installed, problems);
        }
        finally
        {
            IsBusy = false;
        }
        Report("Обновлено", installed, problems);
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        if (Profile is not { } profile || IsBusy) return;
        var todo = Rows.Where(r => r.CanUpdate).ToList();
        if (todo.Count == 0) return;

        var list = string.Join("\n", todo.Select(r => $"• {r.Name}: {r.Installed} → {r.Latest}"));
        if (!Confirm($"Обновить модов: {todo.Count}?\n\n{list}\n\nСтарые версии сохранятся для отката.")) return;
        if (!ConfirmIfRunning(profile)) return;

        var installed = new List<string>();
        var problems = new List<string>();
        IsBusy = true;
        try
        {
            // по одному: понятный прогресс и никаких гонок за папку модов
            foreach (var row in todo)
                await DownloadAndInstallAsync(profile, row.ModId, row.Name, row.Result.LatestCompatible!, installed, problems);
        }
        finally
        {
            IsBusy = false;
        }
        Report("Обновлено", installed, problems);
    }

    // ---------- откат / другая версия ----------

    [RelayCommand]
    private async Task RollbackAsync(ModRowViewModel? row)
    {
        if (row?.Local.Info is not { } info || Profile is not { } profile || IsBusy) return;

        IReadOnlyList<ModDbRelease> releases = [];
        IsBusy = true;
        try
        {
            StatusText = $"Ищу версии «{row.Name}»…";
            var remote = row.Result.Remote ?? await _db.GetModAsync(info.ModId);
            if (remote is not null && profile.GameVersion is { } game)
                releases = UpdateChecker.CompatibleReleases(remote.Releases, game);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // без сети — только сохранённые копии
        }
        finally
        {
            IsBusy = false;
            StatusText = "";
        }

        var store = ModBackupStore.ForProfile(profile.Profile);
        var dlg = new RollbackWindow($"{row.Name} — сейчас {row.Installed}",
            RollbackWindow.BuildOptions(store, info.ModId, info.Version, releases))
        {
            Owner = Application.Current.MainWindow,
        };
        if (dlg.ShowDialog() != true || dlg.Selected is not { } choice) return;
        if (!ConfirmIfRunning(profile)) return;

        var installed = new List<string>();
        var problems = new List<string>();
        IsBusy = true;
        try
        {
            if (choice.Path is not null)
                InstallZip(profile, choice.Path, interactive: false, installed, problems);
            else if (choice.Release is not null)
                await DownloadAndInstallAsync(profile, info.ModId, row.Name, choice.Release, installed, problems);
        }
        finally
        {
            IsBusy = false;
        }
        Report("Установлено", installed, problems);
    }

    // ---------- зависимости ----------

    [RelayCommand]
    private async Task FixDependenciesAsync()
    {
        if (Profile is not { } start || IsBusy || DependencyIssues.Count == 0) return;
        if (start.GameVersion is not { } game)
        {
            Error("Не определена версия игры — укажи папку игры в настройках профиля.");
            return;
        }

        var list = string.Join("\n", DependencyIssues.Select(i => "• " + i.Describe()));
        if (!Confirm($"Исправить зависимости?\n\n{list}\n\nНедостающие и устаревшие скачаются из модбазы, выключенные включатся.")) return;
        if (!ConfirmIfRunning(start)) return;

        var installed = new List<string>();
        var problems = new List<string>();
        IsBusy = true;
        try
        {
            await FixDependencyIssuesAsync(game, installed, problems);
        }
        finally
        {
            IsBusy = false;
        }
        Report("Зависимости", installed, problems);
    }

    /// <summary>
    /// Докачивает недостающие/устаревшие зависимости и включает выключенные. Несколько кругов:
    /// у зависимостей бывают свои зависимости. Без вопросов — вызывающий уже спросил.
    /// </summary>
    private async Task FixDependencyIssuesAsync(ModVersion game, List<string> installed, List<string> problems)
    {
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var round = 0; round < 5; round++)
            {
                ReloadLocal();
                var issues = DependencyIssues.Where(i => tried.Add(i.ModId)).ToList();
                if (issues.Count == 0 || Profile is not { } profile) break;

                foreach (var issue in issues)
                {
                    if (issue.IsDisabled)
                    {
                        var mod = _locals.First(l => string.Equals(l.Info?.ModId, issue.ModId, StringComparison.OrdinalIgnoreCase));
                        ModConfigEditor.SetEnabled(profile, mod.Info!, enabled: true);
                        installed.Add($"{mod.Info!.Name}: включён");
                        continue;
                    }

                    StatusText = $"Ищу {issue.ModId} в модбазе…";
                    var found = await _updater.FindBestReleaseAsync(issue.ModId, game, _main.AllowUnstable, profile.Profile.ToPolicy());
                    if (found is not { } f)
                    {
                        problems.Add($"{issue.ModId}: нет в модбазе или нет версии для {game.Major}.{game.Minor}.x — поставь вручную");
                        continue;
                    }
                    await DownloadAndInstallAsync(profile, issue.ModId, f.Mod.Name ?? issue.ModId, f.Release, installed, problems);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            problems.Add(ex.Message);
        }
    }

    // ---------- установка из каталога ----------

    /// <summary>modid установленных модов активного профиля (для отметки «установлен» в каталоге).</summary>
    public IReadOnlyDictionary<string, string> InstalledVersions =>
        _locals.Where(l => l.Info is not null)
            .GroupBy(l => l.Info!.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Info!.Version ?? "", StringComparer.OrdinalIgnoreCase);

    public event EventHandler? LocalModsChanged;

    /// <summary>Скачать и поставить релиз из каталога, затем предложить доставить его зависимости.</summary>
    public async Task<bool> InstallFromCatalogAsync(string modId, string name, ModDbRelease release)
    {
        if (Profile is not { } profile || IsBusy) return false;
        if (!ConfirmIfRunning(profile)) return false;

        var installed = new List<string>();
        var problems = new List<string>();
        IsBusy = true;
        try
        {
            if (!await DownloadAndInstallAsync(profile, modId, name, release, installed, problems))
            {
                Report("Установка", installed, problems);
                return false;
            }

            ReloadLocal();
            if (DependencyIssues.Count > 0 && profile.GameVersion is { } game)
            {
                var list = string.Join("\n", DependencyIssues.Select(i => "• " + i.Describe()));
                if (Confirm($"«{name}» установлен. Не хватает зависимостей:\n\n{list}\n\nДоставить их из модбазы?"))
                    await FixDependencyIssuesAsync(game, installed, problems);
            }
        }
        finally
        {
            IsBusy = false;
        }
        Report("Установлено", installed, problems);
        return true;
    }

    // ---------- закрепление и пропуск версий ----------

    [RelayCommand]
    private void TogglePin(ModRowViewModel? row)
    {
        if (row?.Local.Info is not { } info || _main.ActiveProfile is not { } profile) return;
        var pins = profile.Model.PinnedMods;
        if (pins.Remove(info.ModId))
            StatusText = $"«{row.Name}» откреплён — обновления снова предлагаются";
        else
        {
            pins[info.ModId] = info.Version ?? "";
            StatusText = $"«{row.Name}» закреплён на {info.Version} — обновления не предлагаются";
        }
        _main.SaveSettings();
        Rebuild();
    }

    [RelayCommand]
    private void SkipVersion(ModRowViewModel? row)
    {
        if (row?.Local.Info is not { } info || _main.ActiveProfile is not { } profile) return;
        if (row.Kind != ModStatus.UpdateAvailable || row.Result.LatestCompatible?.ModVersion is not { } version)
        {
            StatusText = "Пропустить можно только предлагаемое обновление";
            return;
        }
        if (!profile.Model.BlockedVersions.TryGetValue(info.ModId, out var list))
            profile.Model.BlockedVersions[info.ModId] = list = [];
        list.Add(version);
        _main.SaveSettings();
        StatusText = $"«{row.Name}» {version} пропущена — следующая версия снова будет предложена";
        Rebuild();
    }

    [RelayCommand]
    private void ClearSkipped(ModRowViewModel? row)
    {
        if (row?.Local.Info is not { } info || _main.ActiveProfile is not { } profile) return;
        if (profile.Model.BlockedVersions.Remove(info.ModId))
        {
            _main.SaveSettings();
            StatusText = $"«{row.Name}»: пропущенные версии снова предлагаются";
            Rebuild();
        }
        else StatusText = $"У «{row.Name}» нет пропущенных версий";
    }


    // ---------- прочее ----------

    [RelayCommand]
    private async Task ShowInCatalog(ModRowViewModel? row)
    {
        if (row is null) return;
        await _main.ShowInCatalogAsync(row.Result.Remote?.AssetId, row.ModId, row.Local.Info?.Name);
    }

    [RelayCommand]
    private static void OpenPage(ModRowViewModel? row)
    {
        if (row?.PageUrl is { } url) Shell.OpenUrl(url);
    }

    [RelayCommand]
    private static void ShowFile(ModRowViewModel? row)
    {
        if (row is not null && Path.Exists(row.FilePath)) Shell.ShowInFolder(row.FilePath);
    }

    [RelayCommand]
    private void OpenModsFolder()
    {
        if (Profile?.InstallDir is { } dir && Directory.Exists(dir)) Shell.OpenFolder(dir);
    }

    private static bool Confirm(string text) =>
        MessageBox.Show(Application.Current.MainWindow, text, "eViSTool", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static void Error(string text) =>
        MessageBox.Show(Application.Current.MainWindow, text, "eViSTool", MessageBoxButton.OK, MessageBoxImage.Warning);
}

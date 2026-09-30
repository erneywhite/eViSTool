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
using eViSTool.Core.Localization;
using eViSTool.Core.Packs;

namespace eViSTool.App.ViewModels;

/// <summary>Чипы над таблицей модов.</summary>
public enum ModFilter { All, Updates, Problems, Pinned, Disabled }

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
    [ObservableProperty] private ModFilter _filter = ModFilter.All;
    [ObservableProperty] private string _search = "";

    /// <summary>Сортировка: 0 — сначала важное, 1 — по имени.</summary>
    [ObservableProperty] private int _sortIndex;

    /// <summary>Выбранный в таблице мод и его карточка справа.</summary>
    [ObservableProperty] private ModRowViewModel? _selected;
    [ObservableProperty] private ModCardViewModel? _card;
    private CancellationTokenSource? _cardLoad;

    // счётчики над таблицей и на чипах
    [ObservableProperty] private int _updatesAvailable;
    [ObservableProperty] private int _problemCount;
    [ObservableProperty] private int _pinnedCount;
    [ObservableProperty] private int _disabledCount;

    /// <summary>Идёт скачивание/установка — кнопки операций недоступны.</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Проблемы с зависимостями включённых модов (считаются по диску, без сети).</summary>
    [ObservableProperty] private IReadOnlyList<DependencyIssue> _dependencyIssues = [];
    [ObservableProperty] private int _updateCount;

    /// <summary>Сколько модов в профиле (счётчик в боковой навигации).</summary>
    [ObservableProperty] private int _modCount;

    public bool HasDependencyIssues => DependencyIssues.Count > 0;
    public string DependencyText => DependencyIssues.Count == 0 ? ""
        : string.Join("; ", DependencyIssues.Select(i => i.Describe()));
    public string UpdateAllText => Loc.T("mods.updateAll", UpdateCount);

    public string InstalledLabel => Loc.Plural("mods.cntInstalled", ModCount);
    public string UpdatesLabel => Loc.Plural("mods.cntUpdates", UpdatesAvailable);
    public string ProblemsLabel => Loc.Plural("mods.cntProblems", ProblemCount);
    public string DisabledLabel => Loc.Plural("mods.cntDisabled", DisabledCount);
    public string ShownText => Loc.T("mods.shown", View.Cast<object>().Count(), Rows.Count);

    public IReadOnlyList<string> SortModes { get; } = [Loc.T("mods.sortImportant"), Loc.T("mods.sortName")];

    /// <summary>Ширина карточки (тянется разделителем, запоминается).</summary>
    public double CardWidth
    {
        get => _main.Layout.ModsCardWidth;
        set { _main.Layout.ModsCardWidth = Math.Max(340, value); OnPropertyChanged(); }
    }

    public void SaveLayout() => _main.SaveSettings();

    partial void OnModCountChanged(int value) => OnPropertyChanged(nameof(InstalledLabel));
    partial void OnUpdatesAvailableChanged(int value) => OnPropertyChanged(nameof(UpdatesLabel));
    partial void OnProblemCountChanged(int value) => OnPropertyChanged(nameof(ProblemsLabel));
    partial void OnDisabledCountChanged(int value) => OnPropertyChanged(nameof(DisabledLabel));

    partial void OnSelectedChanged(ModRowViewModel? value)
    {
        // null приходит и когда таблицу перестраивают после операции — тогда карточку не трогаем,
        // её заменит та же строка из новой таблицы
        if (value is null) return;
        _cardLoad?.Cancel();
        _cardLoad = new CancellationTokenSource();
        Card = new ModCardViewModel(value, this, Profile?.GameVersion, _main.ActiveProfile?.Name ?? "");
        _ = Card.LoadAsync(_db, _cardLoad.Token);
    }

    [RelayCommand]
    private void SetFilter(ModFilter filter) => Filter = filter;

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
            && Filter switch
            {
                ModFilter.Updates => r.Kind == ModStatus.UpdateAvailable,
                ModFilter.Problems => r.IsProblem,
                ModFilter.Pinned => r.IsPinned,
                ModFilter.Disabled => !r.IsEnabled,
                _ => true,
            }
            && (Search.Length == 0
                || r.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || r.ModId.Contains(Search, StringComparison.OrdinalIgnoreCase));
        ApplySort();
    }

    partial void OnFilterChanged(ModFilter value) => RefreshView();
    partial void OnSearchChanged(string value) => RefreshView();
    partial void OnSortIndexChanged(int value) => ApplySort();

    private void RefreshView()
    {
        View.Refresh();
        OnPropertyChanged(nameof(ShownText));
    }

    private void ApplySort()
    {
        using (View.DeferRefresh())
        {
            View.SortDescriptions.Clear();
            if (SortIndex == 0) View.SortDescriptions.Add(new SortDescription(nameof(ModRowViewModel.Importance), ListSortDirection.Ascending));
            View.SortDescriptions.Add(new SortDescription(nameof(ModRowViewModel.Name), ListSortDirection.Ascending));
        }
    }

    private ResolvedProfile? Profile => _main.ActiveProfile?.Resolved;

    /// <summary>Подзаголовок страницы: «Мой мир · клиент / Vintage Story 1.22.7».</summary>
    public string HeaderSubtitle => _main.ActiveProfile is { } p
        ? Loc.T("mods.subtitle", p.Name, p.KindText, Profile?.GameVersion?.ToString() ?? Loc.T("common.notFound"))
        : Loc.T("mods.noProfile");

    /// <summary>Строка под кнопками: какие папки и какая игра.</summary>
    public string ProfileInfo => Profile is { } r
        ? Loc.T("mods.profileInfo", r.GameVersion?.ToString() ?? Loc.T("common.notFound"), string.Join("; ", r.ModDirs))
        : Loc.T("mods.noProfile");

    /// <summary>Сменился профиль или его настройки — перечитываем моды с диска, ответ модбазы про другой набор сбрасываем.</summary>
    public void OnProfileSwitched()
    {
        _remote = new(StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(ProfileInfo));
        OnPropertyChanged(nameof(HeaderSubtitle));
        Selected = null;
        Card = null;
        ReloadLocal();
        StatusText = Loc.T("mods.readFromDisk");
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
            StatusText = Loc.T("mods.readFailed", ex.Message);
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
        ModCount = results.Count;
        DependencyIssues = Dependencies.FindIssues(_locals);

        // после операции таблица строится заново — выбор остаётся на том же моде
        var keep = Card?.Row;
        var pins = _main.ActiveProfile?.Model.PinnedMods ?? new Dictionary<string, string>();
        Rows.Clear();
        foreach (var r in results)
            Rows.Add(new ModRowViewModel(r, DependencyIssues, r.Local.Info?.ModId is { } id && pins.ContainsKey(id)));

        UpdatesAvailable = Rows.Count(r => r.Kind == ModStatus.UpdateAvailable);
        ProblemCount = Rows.Count(r => r.IsProblem);
        PinnedCount = Rows.Count(r => r.IsPinned);
        DisabledCount = Rows.Count(r => !r.IsEnabled);
        OnPropertyChanged(nameof(ShownText));

        if (keep is not null)
        {
            var same = Rows.FirstOrDefault(r => r.FilePath == keep.FilePath)
                       ?? Rows.FirstOrDefault(r => r.ModId.Length > 0 && r.ModId == keep.ModId);
            if (same is null) Card = null; // мод удалили
            else if (same == Selected) OnSelectedChanged(same);
            else Selected = same;
        }
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
                StatusText = Loc.T("mods.noGameVersion");
                return;
            }
            var progress = new Progress<string>(s => StatusText = s);
            _remote = await _service.FetchRemoteAsync(_locals, progress, ct);
            Rebuild();
            StatusText = Loc.T("mods.checkedAt", DateTime.Now.ToString("HH:mm:ss"), game);
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.T("mods.checkCancelled");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            StatusText = Loc.T("common.errorWith", ex.Message);
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
                ? Loc.T("mods.toggleServerRunning")
                : Loc.T("mods.toggleGameRunning");
            if (!Confirm(Loc.T(row.IsEnabled ? "mods.toggleAnywayDisable" : "mods.toggleAnywayEnable", what, row.Name)))
            {
                ReloadLocal(); // вернуть галочку как было
                return;
            }
        }

        try
        {
            ModConfigEditor.SetEnabled(profile, info, enabled: !row.IsEnabled);
            StatusText = Loc.T(row.IsEnabled ? "mods.disabledOk" : "mods.enabledOk", row.Name);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Error(Loc.T("mods.toggleFailed", ex.Message));
        }
        ReloadLocal();
    }

    // ---------- удаление ----------

    [RelayCommand]
    private void Delete(ModRowViewModel? row)
    {
        if (row is null || Profile is not { } profile) return;

        var running = GameProcess.IsRunning(profile.Profile)
            ? "\n\n" + Loc.T("mods.deleteRunning") : "";
        if (!Confirm(Loc.T("mods.deleteConfirm", row.Name, row.Installed) + running))
            return;

        try
        {
            Shell.MoveToRecycleBin(row.Local.Path);
            // если других копий мода не осталось — чистим его и из списка выключенных
            if (row.Local.Info is { } info && _locals.Count(l => l.Info?.ModId == info.ModId) == 1)
                ModConfigEditor.Forget(profile, info);
            StatusText = Loc.T("mods.deletedOk", row.Name);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            Error(Loc.T("mods.deleteFailed", row.Name, ex.Message));
        }
        ReloadLocal();
    }

    // ---------- добавление из архива ----------

    [RelayCommand]
    private void AddFromFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("mods.pickZipTitle"),
            Filter = Loc.T("mods.zipFilter") + " (*.zip)|*.zip",
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true) AddFiles(dlg.FileNames);
    }

    /// <summary>Установка набора zip (кнопка или перетаскивание).</summary>
    public void AddFiles(IEnumerable<string> files)
    {
        if (Profile is not { } profile) return;

        var list = files.ToList();
        if (list.FirstOrDefault(f => f.EndsWith(PackManifest.Extension, StringComparison.OrdinalIgnoreCase)) is { } pack)
        {
            _ = ImportPackFileAsync(pack);
            return;
        }
        var zips = list.Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        if (zips.Count == 0)
        {
            Error(Loc.T("mods.notZip"));
            return;
        }

        if (GameProcess.IsRunning(profile.Profile)
            && !Confirm(Loc.T("mods.runningContinue")))
            return;

        var installed = new List<string>();
        var problems = new List<string>();
        foreach (var zip in zips)
            InstallZip(profile, zip, interactive: true, installed, problems);

        ReloadLocal();
        if (installed.Count > 0) StatusText = Loc.T("report.installed") + ": " + string.Join("; ", installed);

        if (problems.Count > 0)
        {
            var text = (installed.Count > 0 ? Loc.T("report.installed") + ":\n• " + string.Join("\n• ", installed) + "\n\n" : "")
                       + Loc.T("report.attention") + ":\n• " + string.Join("\n• ", problems);
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

            if (interactive && plan.IsSameVersion && !Confirm(Loc.T("mods.reinstallConfirm", info.Name, info.Version)))
                return false;
            if (interactive && plan.IsDowngrade && !Confirm(Loc.T("mods.downgradeConfirm", info.Name, old, info.Version)))
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
            problems.AddRange(DependencyIssues.Select(i => Loc.T("report.dependency", i.Describe())));
        StatusText = installed.Count switch
        {
            0 => Loc.T("report.nothingChanged", title),
            <= 3 => $"{title}: " + string.Join("; ", installed),
            _ => Loc.T("report.changedCount", title, installed.Count), // длинный список — только число
        };
        if (problems.Count == 0) return;
        var text = (installed.Count > 0 ? $"{title}:\n• " + string.Join("\n• ", installed) + "\n\n" : "")
                   + Loc.T("report.attention") + ":\n• " + string.Join("\n• ", problems);
        MessageBox.Show(Application.Current.MainWindow, text, "eViSTool", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static bool ConfirmIfRunning(ResolvedProfile profile) =>
        !GameProcess.IsRunning(profile.Profile)
        || Confirm(Loc.T("mods.runningContinue"));

    /// <summary>Скачать релиз и поставить его. Ошибки — в problems.</summary>
    private async Task<bool> DownloadAndInstallAsync(ResolvedProfile profile, string modId, string name, ModDbRelease release,
        List<string> installed, List<string> problems)
    {
        string? file = null;
        try
        {
            var progress = new Progress<double>(x => StatusText = Loc.T("dl.progress", name, release.ModVersion, x.ToString("P0")));
            file = await _updater.DownloadReleaseAsync(release, modId, progress);
            return InstallZip(profile, file, interactive: false, installed, problems);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            problems.Add(Loc.T("dl.failed", name, release.ModVersion, ex.Message));
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
        Report(Loc.T("report.updated"), installed, problems);
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        if (Profile is not { } profile || IsBusy) return;
        var todo = Rows.Where(r => r.CanUpdate).ToList();
        if (todo.Count == 0) return;

        var list = string.Join("\n", todo.Select(r => $"• {r.Name}: {r.Installed} → {r.Latest}"));
        if (!Confirm(Loc.T("mods.updateAllConfirm", todo.Count, list))) return;
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
        Report(Loc.T("report.updated"), installed, problems);
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
            StatusText = Loc.T("mods.findingVersions", row.Name);
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
        var dlg = new RollbackWindow(Loc.T("rollback.heading", row.Name, row.Installed),
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
        Report(Loc.T("report.installed"), installed, problems);
    }

    // ---------- зависимости ----------

    [RelayCommand]
    private async Task FixDependenciesAsync()
    {
        if (Profile is not { } start || IsBusy || DependencyIssues.Count == 0) return;
        if (start.GameVersion is not { } game)
        {
            Error(Loc.T("mods.noGameVersion"));
            return;
        }

        var list = string.Join("\n", DependencyIssues.Select(i => "• " + i.Describe()));
        if (!Confirm(Loc.T("mods.fixDepsConfirm", list))) return;
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
        Report(Loc.T("report.dependencies"), installed, problems);
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
                        installed.Add(Loc.T("mods.enabledShort", mod.Info!.Name));
                        continue;
                    }

                    StatusText = Loc.T("mods.searchingModDb", issue.ModId);
                    var found = await _updater.FindBestReleaseAsync(issue.ModId, game, _main.AllowUnstable, profile.Profile.ToPolicy());
                    if (found is not { } f)
                    {
                        problems.Add(Loc.T("mods.depNotFound", issue.ModId, $"{game.Major}.{game.Minor}.x"));
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
                Report(Loc.T("report.install"), installed, problems);
                return false;
            }

            ReloadLocal();
            if (DependencyIssues.Count > 0 && profile.GameVersion is { } game)
            {
                var list = string.Join("\n", DependencyIssues.Select(i => "• " + i.Describe()));
                if (Confirm(Loc.T("mods.installDepsConfirm", name, list)))
                    await FixDependencyIssuesAsync(game, installed, problems);
            }
        }
        finally
        {
            IsBusy = false;
        }
        Report(Loc.T("report.installed"), installed, problems);
        return true;
    }

    // ---------- модпаки ----------

    [RelayCommand]
    private async Task ExportPackAsync()
    {
        if (Profile is not { } profile || IsBusy) return;
        var enabled = _locals.Count(l => l.Info is not null && !l.IsDisabled);
        var disabled = _locals.Count(l => l.Info is not null && l.IsDisabled);
        if (enabled + disabled == 0)
        {
            Error(Loc.T("packx.noMods"));
            return;
        }

        var hasConfig = profile.Profile.DataDir is { } data && Directory.Exists(Path.Combine(data, "ModConfig"));
        var dlg = new ExportPackWindow(profile.Profile.Name, enabled, disabled, hasConfig) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.Options is not { } options) return;

        var save = new SaveFileDialog
        {
            Title = Loc.T("packx.saveTitle"),
            Filter = Loc.T("pack.filter") + $" (*{PackManifest.Extension})|*{PackManifest.Extension}",
            FileName = string.Concat(options.Name.Split(Path.GetInvalidFileNameChars())) + PackManifest.Extension,
        };
        if (save.ShowDialog() != true) return;

        IsBusy = true;
        try
        {
            // что есть в модбазе — можно не класть внутрь; для этого нужен ответ модбазы
            if (!options.BundleFiles && _remote.Count == 0)
            {
                StatusText = Loc.T("check.querying", 0, _locals.Count);
                _remote = await _service.FetchRemoteAsync(_locals, new Progress<string>(s => StatusText = s));
            }
            var onModDb = _remote.Where(r => r.Value.Mod is not null).Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            StatusText = Loc.T("packx.building");
            var locals = _locals;
            var manifest = await Task.Run(() => PackBuilder.Build(profile, locals, options, onModDb, save.FileName, $"eViSTool {AppVersion}"));
            var size = new FileInfo(save.FileName).Length / 1024.0 / 1024.0;
            StatusText = Loc.T("packx.done", manifest.Mods.Count, size.ToString("0.0"), save.FileName);
            Shell.ShowInFolder(save.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            Error(Loc.T("packx.failed", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportPackAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("packi.openTitle"),
            Filter = Loc.T("pack.filter") + $" (*{PackManifest.Extension})|*{PackManifest.Extension}",
        };
        if (dlg.ShowDialog() == true) await ImportPackFileAsync(dlg.FileName);
    }

    public async Task ImportPackFileAsync(string path)
    {
        if (Profile is not { } profile || IsBusy) return;

        PackFile pack;
        try
        {
            pack = PackFile.Open(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Newtonsoft.Json.JsonException)
        {
            Error(Loc.T("packi.openFailed", ex.Message));
            return;
        }

        using (pack)
        {
            var plan = PackImporter.Plan(pack.Manifest, profile, ModUpdateService.ScanLocal(profile));
            var dlg = new ImportPackWindow(plan) { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() != true) return;
            if (!ConfirmIfRunning(profile)) return;

            IsBusy = true;
            PackImportResult result;
            try
            {
                var importer = new PackImporter(_db, _updater);
                var progress = new Progress<string>(s => StatusText = s);
                var (mirror, applyConfig) = (dlg.Mirror, dlg.ApplyConfig);
                // в фоне: иначе при паке «всё внутри» импорт идёт синхронно, окно подвисает,
                // а сообщения прогресса приходят уже после итога и затирают его
                result = await Task.Run(() => importer.ApplyAsync(pack, plan, profile, mirror, applyConfig,
                    ModBackupStore.ForProfile(profile.Profile), progress));
            }
            finally
            {
                IsBusy = false;
            }
            Report(Loc.T("report.imported", pack.Manifest.Name), result.Done.ToList(), result.Problems.ToList());
        }
    }

    private static string AppVersion =>
        typeof(ModsViewModel).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "";

    // ---------- закрепление и пропуск версий ----------

    [RelayCommand]
    private void TogglePin(ModRowViewModel? row)
    {
        if (row?.Local.Info is not { } info || _main.ActiveProfile is not { } profile) return;
        var pins = profile.Model.PinnedMods;
        if (pins.Remove(info.ModId))
            StatusText = Loc.T("mods.unpinned", row.Name);
        else
        {
            pins[info.ModId] = info.Version ?? "";
            StatusText = Loc.T("mods.pinned", row.Name, info.Version);
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
            StatusText = Loc.T("mods.skipOnlyOffered");
            return;
        }
        if (!profile.Model.BlockedVersions.TryGetValue(info.ModId, out var list))
            profile.Model.BlockedVersions[info.ModId] = list = [];
        list.Add(version);
        _main.SaveSettings();
        StatusText = Loc.T("mods.skipped", row.Name, version);
        Rebuild();
    }

    [RelayCommand]
    private void ClearSkipped(ModRowViewModel? row)
    {
        if (row?.Local.Info is not { } info || _main.ActiveProfile is not { } profile) return;
        if (profile.Model.BlockedVersions.Remove(info.ModId))
        {
            _main.SaveSettings();
            StatusText = Loc.T("mods.skipCleared", row.Name);
            Rebuild();
        }
        else StatusText = Loc.T("mods.noSkipped", row.Name);
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

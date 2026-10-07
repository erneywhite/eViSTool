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
using eViSTool.Core.Server;
using System.Windows.Threading;

namespace eViSTool.App.ViewModels;

/// <summary>Чипы над таблицей модов.</summary>
public enum ModFilter { All, Updates, Problems, Pinned, Disabled, Unneeded }

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

    /// <summary>Клиентские моды в серверном профиле — фильтр виден, только когда они есть.</summary>
    [ObservableProperty] private int _unneededCount;
    public bool HasUnneeded => UnneededCount > 0;
    partial void OnUnneededCountChanged(int value) => OnPropertyChanged(nameof(HasUnneeded));

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
        // тот же мод после операции — остаёмся на той же вкладке карточки
        var keepTab = Card is { IsVersionsTab: true } old && old.Row.ModId == value.ModId;
        Card = new ModCardViewModel(value, this, Profile?.GameVersion, _main.ActiveProfile?.Name ?? "");
        if (keepTab) Card.IsVersionsTab = true;
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
                ModFilter.Unneeded => r.IsUnneeded,
                _ => true,
            }
            && (Search.Length == 0
                || r.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || r.ModId.Contains(Search, StringComparison.OrdinalIgnoreCase));
        ApplySort();
        _diskWatch.Tick += (_, _) =>
        {
            if (IsRemote || IsBusy || Profile is not { } profile) return;
            if (ServerMods.StampOf(profile) == _diskStamp) return;
            ReloadLocal();
            StatusText = Loc.T("mods.changedOutside");
        };
    }

    /// <summary>Вкладка «Мои моды» открыта — следим за папками модов (закрыта — не тратим на это время).</summary>
    public void SetActive(bool active)
    {
        if (active && !_diskWatch.IsEnabled)
        {
            if (!IsRemote && Profile is { } profile && ServerMods.StampOf(profile) != _diskStamp) ReloadLocal();
            _diskWatch.Start();
        }
        else if (!active) _diskWatch.Stop();
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

    /// <summary>Моды берутся с диска этой машины или — у удалённого сервера — у его агента по сети.</summary>
    private ResolvedProfile? Profile => IsRemote ? _remoteResolved : _main.ActiveProfile?.Resolved;

    /// <summary>
    /// Версия игры активного профиля — одна на всё окно (каталог берёт её отсюда): у своего — по папке игры,
    /// у удалённого — та, что сообщил его агент. null — неизвестна (агент ещё не ответил, папка игры не найдена).
    /// </summary>
    public ModVersion? GameVersion => Profile?.GameVersion;

    // ---- удалённый сервер: список модов и папки — со слов агента, пути — на той машине
    private bool IsRemote => _main.ActiveProfile?.Model.IsRemote == true;
    private ResolvedProfile? _remoteResolved;
    private DateTime? _remoteStamp;
    private bool _remoteLoading;

    /// <summary>Профиль на этой машине: папку модов можно открыть, модпаки — собрать и поставить.</summary>
    public bool IsLocalProfile => !IsRemote;

    /// <summary>
    /// Цель для действия, начатого сейчас: снимок активного профиля. Дальше операция работает только с ней —
    /// переключение профиля посреди загрузки её не сдвинет (раньше мод мог уйти в прежний профиль).
    /// </summary>
    private ModTarget? CurrentTarget()
    {
        if (_main.ActiveProfile is not { } profile) return null;
        if (ModTarget.For(profile.Model) is { } target) return target;
        Error(Loc.T("remote.errDecrypt"));
        return null;
    }

    private bool IsActive(ModTarget target) => target.ProfileId == _main.ActiveProfile?.Model.Id;

    // ---- своя машина: моды могли поменять в другом окне (удалённом или втором локальном) — перечитываем сами
    private readonly DispatcherTimer _diskWatch = new() { Interval = TimeSpan.FromSeconds(2) };
    private DateTime? _diskStamp;

    /// <summary>Подзаголовок страницы: «Мой мир · клиент / Vintage Story 1.22.7».</summary>
    public string HeaderSubtitle => _main.ActiveProfile is { } p
        ? Loc.T("mods.subtitle", p.Name, p.KindText, Profile?.GameVersion?.ToString() ?? Loc.T("common.notFound"))
        : Loc.T("mods.noProfile");

    /// <summary>Строка под кнопками: какие папки и какая игра.</summary>
    public string ProfileInfo => Profile is { } r
        ? Loc.T("mods.profileInfo", r.GameVersion?.ToString() ?? Loc.T("common.notFound"), string.Join("; ", r.ModDirs))
        : IsRemote ? Loc.T("mods.remoteWaiting")
        : Loc.T("mods.noProfile");

    /// <summary>Сменился профиль или его настройки — перечитываем моды с диска, ответ модбазы про другой набор сбрасываем.</summary>
    public void OnProfileSwitched()
    {
        _remote = new(StringComparer.OrdinalIgnoreCase);
        _remoteResolved = null;
        _remoteStamp = null;
        _locals = [];
        OnPropertyChanged(nameof(IsLocalProfile));
        OnPropertyChanged(nameof(ProfileInfo));
        OnPropertyChanged(nameof(HeaderSubtitle));
        Selected = null;
        Card = null;
        if (IsRemote)
        {
            // список придёт с первым ответом агента (OnRemoteStatus)
            Rebuild();
            StatusText = Loc.T("mods.remoteWaiting");
            return;
        }
        ReloadLocal();
        StatusText = Loc.T("mods.readFromDisk");
        if (_main.AutoCheckUpdates && Profile?.GameVersion is not null && CheckCommand.CanExecute(null))
            CheckCommand.Execute(null);
    }

    /// <summary>Перечитать папки модов (без сети) и перестроить таблицу с уже известными статусами.</summary>
    public void ReloadLocal()
    {
        if (IsRemote)
        {
            _ = ReloadAsync();
            return;
        }
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
        _diskStamp = ServerMods.StampOf(profile.Resolved);
        Rebuild();
        LocalModsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Перечитать моды и дождаться: у своей машины — с диска, у удалённого сервера — у агента.</summary>
    private async Task ReloadAsync()
    {
        if (!IsRemote)
        {
            ReloadLocal();
            return;
        }
        if (_main.Server.RemoteClient is not { } agent) return; // нет связи — список обновится, когда она появится
        var profile = _main.ActiveProfile!.Model;
        _remoteLoading = true;
        try
        {
            var list = await agent.ModsAsync();
            if (_main.ActiveProfile?.Model != profile) return; // пока ждали ответ, переключили профиль
            var first = _remoteResolved is null;
            _remoteResolved = new ResolvedProfile
            {
                Profile = profile,
                GameVersion = ModVersion.ParseOrNull(list.GameVersion),
                ModDirs = list.ModDirs,
                InstallDir = list.InstallDir,
                DisabledMods = list.DisabledMods,
            };
            var disabled = new HashSet<string>(list.DisabledMods);
            _locals = [.. list.Mods.Select(m => new LocalMod(m.Path, m.Info, m.Error))
                .Select(l => l with { IsDisabled = ModUpdateService.IsDisabled(l, disabled) })];
            _remoteStamp = list.ChangedAt;
            if (first)
            {
                OnPropertyChanged(nameof(ProfileInfo));
                OnPropertyChanged(nameof(HeaderSubtitle));
                if (StatusText == Loc.T("mods.remoteWaiting")) StatusText = Loc.T("mods.readFromServer");
            }
            Rebuild();
            LocalModsChanged?.Invoke(this, EventArgs.Empty);
            if (first && _main.AutoCheckUpdates && _remoteResolved.GameVersion is not null && CheckCommand.CanExecute(null))
                CheckCommand.Execute(null);
            await CountRemoteConfigsAsync(agent, profile);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            StatusText = Loc.T("mods.readFailed", ex.Message);
        }
        finally
        {
            _remoteLoading = false;
        }
    }

    /// <summary>Новый статус агента удалённого сервера: моды там поменялись (здесь или в другом окне) — перечитываем.</summary>
    public void OnRemoteStatus(AgentStatus status)
    {
        // своё действие ещё не закончилось — его изменение не «чужое», список перечитает оно само
        if (!IsRemote || _remoteLoading || IsBusy || ToggleEnabledCommand.IsRunning || DeleteCommand.IsRunning) return;
        if (_remoteResolved is null) _ = ReloadAsync();
        else if (status.ModsChangedAt != _remoteStamp) _ = ReloadChangedAsync();
    }

    private async Task ReloadChangedAsync()
    {
        await ReloadAsync();
        StatusText = Loc.T("mods.changedOutside");
    }

    /// <summary>Игра или сервер цели запущены (свой — по процессу, удалённый — со слов агента, если это активный профиль).</summary>
    private bool IsRunning(ModTarget target) =>
        target.IsRemote ? IsActive(target) && _main.Server.IsServerUp : GameProcess.IsRunning(target.Profile);

    // ---------- настройки модов ----------

    /// <summary>Сколько файлов настроек (ModConfig) у каждого мода профиля: modid → число.</summary>
    private Dictionary<string, int> _configCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>У удалённого сервера — со слов агента (запрашиваются после списка модов).</summary>
    private Dictionary<string, int> _remoteConfigCounts = new(StringComparer.OrdinalIgnoreCase);

    private void CountConfigs()
    {
        _configCounts = IsRemote ? _remoteConfigCounts : new(StringComparer.OrdinalIgnoreCase);
        if (IsRemote || Profile?.Profile is not { } p) return;
        var data = string.IsNullOrWhiteSpace(p.DataDir) ? GameInstall.DefaultDataDir : p.DataDir;
        try
        {
            foreach (var g in ModConfigs.List(data, _locals).Where(f => f.ModId is not null).GroupBy(f => f.ModId!))
                _configCounts[g.Key] = g.Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // не прочитали папку — просто без ссылки в карточке
        }
    }

    public int ConfigCount(string modId) => _configCounts.GetValueOrDefault(modId);

    /// <summary>Конфиги удалённого сервера — для ссылки «Настройки мода» в карточке. Агент старой версии их не знает — без ссылки.</summary>
    private async Task CountRemoteConfigsAsync(AgentClient agent, GameProfile profile)
    {
        try
        {
            var counts = (await agent.ModConfigsAsync()).Where(f => f.ModId is not null)
                .GroupBy(f => f.ModId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            if (_main.ActiveProfile?.Model != profile) return;
            _remoteConfigCounts = counts;
            _configCounts = counts;
            Card?.NotifyConfigs();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
        }
    }

    /// <summary>Карточка → вкладка «Настройки модов», сразу на файлах этого мода.</summary>
    [RelayCommand]
    private void OpenModConfig(ModRowViewModel? row)
    {
        if (row is not null) _main.OpenModConfig(row.ModId, row.Name);
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
        CountConfigs();

        // после операции таблица строится заново — выбор остаётся на том же моде
        var keep = Card?.Row;
        var pins = _main.ActiveProfile?.Model.PinnedMods ?? new Dictionary<string, string>();
        var kind = _main.ActiveProfile?.Model.Kind ?? ProfileKind.Client;
        Rows.Clear();
        foreach (var r in results)
            Rows.Add(new ModRowViewModel(r, DependencyIssues, r.Local.Info?.ModId is { } id && pins.ContainsKey(id), kind, Profile?.GameVersion));

        UpdatesAvailable = Rows.Count(r => r.Kind == ModStatus.UpdateAvailable);
        ProblemCount = Rows.Count(r => r.IsProblem);
        PinnedCount = Rows.Count(r => r.IsPinned);
        DisabledCount = Rows.Count(r => !r.IsEnabled);
        UnneededCount = Rows.Count(r => r.IsUnneeded);
        if (UnneededCount == 0 && Filter == ModFilter.Unneeded) Filter = ModFilter.All; // таких модов не осталось — показываем всё
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
            await ReloadAsync();
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
    private async Task ToggleEnabledAsync(ModRowViewModel? row)
    {
        if (row?.Local.Info is null || CurrentTarget() is not { } target) return;

        if (IsRunning(target))
        {
            // клиент при выходе перезаписывает свои настройки, сервер применит только после перезапуска
            var server = target.Profile.Kind == ProfileKind.Server;
            var what = server
                ? Loc.T("mods.toggleServerRunning")
                : Loc.T("mods.toggleGameRunning");
            if (!Confirm(Loc.T(row.IsEnabled ? "mods.toggleAnywayDisable" : "mods.toggleAnywayEnable", what, row.Name)))
            {
                ReloadLocal(); // вернуть галочку как было
                return;
            }
        }

        await CopyWorldsAsync(target);
        try
        {
            await ModTargets.SetEnabledAsync(target, row.Local, enabled: !row.IsEnabled);
            StatusText = WithWorldCopies(Loc.T(row.IsEnabled ? "mods.disabledOk" : "mods.enabledOk", row.Name));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
                                       or HttpRequestException or TaskCanceledException)
        {
            Error(Loc.T("mods.toggleFailed", ex.Message));
        }
        await ReloadAsync();
    }

    // ---------- удаление ----------

    [RelayCommand]
    private async Task DeleteAsync(ModRowViewModel? row)
    {
        if (row is null || Profile is not { } profile || CurrentTarget() is not { } target) return;

        var running = IsRunning(target)
            ? "\n\n" + Loc.T("mods.deleteRunning") : "";
        if (!Confirm(Loc.T("mods.deleteConfirm", row.Name, row.Installed) + running))
            return;
        await CopyWorldsAsync(target);

        try
        {
            if (target.Remote is { } code)
            {
                using var agent = AgentClient.ForRemote(code);
                await agent.DeleteModAsync(row.Local.Path); // на сервере — в его корзину
            }
            else
            {
                Shell.MoveToRecycleBin(row.Local.Path);
                // если других копий мода не осталось — чистим его и из списка выключенных
                if (row.Local.Info is { } info && _locals.Count(l => l.Info?.ModId == info.ModId) == 1)
                    ModConfigEditor.Forget(profile, info);
            }
            StatusText = WithWorldCopies(Loc.T("mods.deletedOk", row.Name));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException
                                       or HttpRequestException or InvalidOperationException)
        {
            Error(Loc.T("mods.deleteFailed", row.Name, ex.Message));
        }
        await ReloadAsync();
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
        if (dlg.ShowDialog() == true) _ = AddFilesAsync(dlg.FileNames);
    }

    /// <summary>Установка набора zip (кнопка или перетаскивание).</summary>
    public async Task AddFilesAsync(IEnumerable<string> files)
    {
        if (Profile is null || IsBusy) return;

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

        if (CurrentTarget() is not { } target || !ConfirmIfRunning(target)) return;
        // моды, нужные и в других профилях (сервер ↔ клиент), — предложить поставить и туда
        var also = await PickAlsoForFilesAsync(target, zips);
        if (also is null) return; // отменили
        also = [.. also.Where(a => ConfirmIfRunning(a.Target))];

        var installed = new List<string>();
        var problems = new List<string>();
        IsBusy = true;
        try
        {
            foreach (var zip in zips)
                await InstallZipAsync(target, zip, interactive: true, installed, problems);
            foreach (var a in also)
                foreach (var (path, _) in a.ToInstall)
                    await Labelled(a.Target, label: true, installed, problems,
                        () => InstallZipAsync(a.Target, path, interactive: false, installed, problems));
        }
        finally
        {
            IsBusy = false;
        }

        await ReloadAsync();
        if (installed.Count > 0) StatusText = Loc.T("report.installed") + ": " + string.Join("; ", installed);

        if (problems.Count > 0)
        {
            var text = (installed.Count > 0 ? Loc.T("report.installed") + ":\n• " + string.Join("\n• ", installed) + "\n\n" : "")
                       + Loc.T("report.attention") + ":\n• " + string.Join("\n• ", problems);
            MessageBox.Show(Application.Current.MainWindow, text, "eViSTool", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Окно «Установить также в» для добавленных файлов. Пустой список — только сюда, null — отмена.</summary>
    private async Task<IReadOnlyList<AlsoInstallFiles>?> PickAlsoForFilesAsync(ModTarget target, IReadOnlyList<string> zips)
    {
        if (_main.ActiveProfile is not { } active) return [];
        var files = zips.Select(z => (Path: z, ModScanner.ReadZip(z).Info))
            .Where(f => f.Info is not null && AlsoInstall.WorthOffering(ModSides.Parse(f.Info.Side)))
            .Select(f => (f.Path, f.Info!)).ToList();
        if (files.Count == 0) return [];
        var others = _main.Profiles
            .Where(p => p != active && files.Any(f => AlsoInstall.Needed(ModSides.Parse(f.Item2.Side), p.Model.Kind)))
            .Select(p => (Profile: p, Target: ModTarget.For(p.Model)))
            .Where(x => x.Target is not null)
            .ToList();
        if (others.Count == 0) return [];

        StatusText = Loc.T("also.checking");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var options = await Task.WhenAll(others.Select(x => AlsoInstall.EvaluateFilesAsync(x.Target!, files, cts.Token)));
        StatusText = "";

        var names = string.Join(", ", files.Select(f => f.Item2.Name));
        var chosen = AlsoInstallWindow.Choose(_main, active, Loc.T("also.hintFiles", names),
            Loc.T("also.thisProfile") + " · " + AlsoInstall.GameText(Profile?.GameVersion),
            [.. options.Select((o, i) => new AlsoChoice(others[i].Profile, o, o.Describe(), o.CanInstall,
                o.Error is null && o.ToInstall.Count == 0, active.Model.IsLinkedTo(others[i].Profile.Model)))]);
        return chosen?.Cast<AlsoInstallFiles>().ToList();
    }

    private readonly List<string> _copiedWorlds = [];

    /// <summary>Итог операции + «сделана копия мира: …», если была; список копий после этого сбрасывается.</summary>
    private string WithWorldCopies(string text)
    {
        if (_copiedWorlds.Count == 0) return text;
        var note = Loc.T("wcopy.done", string.Join(", ", _copiedWorlds));
        _copiedWorlds.Clear();
        return string.IsNullOrEmpty(text) ? note : text + " · " + note;
    }

    /// <summary>
    /// Перед изменением модов игрового профиля — копии одиночных миров, в которые играли с прошлой копии (настройка).
    /// Повторный вызов в той же операции ничего не копирует: миры с тех пор не менялись. Сбой копии изменение
    /// не останавливает — уходит в problems (нет списка — в строку статуса). Свои серверы и удалённые — не трогаем:
    /// у них свои копии мира по расписанию.
    /// </summary>
    private async Task CopyWorldsAsync(ModTarget target, List<string>? problems = null)
    {
        // скопированные миры — в итоговую строку статуса операции (иначе её сразу затрёт «мод выключен» и т. п.)
        if (!_main.CopyWorldsBeforeModChanges || target.IsRemote || target.Profile.Kind != ProfileKind.Client) return;
        var data = string.IsNullOrWhiteSpace(target.Profile.DataDir) ? GameInstall.DefaultDataDir : target.Profile.DataDir;
        if (!WorldCopies.AnyToCopy(data)) return;
        void Note(string text)
        {
            if (problems is null) StatusText = text;
            else if (!problems.Contains(text)) problems.Add(text);
        }
        // игра пишет в мир прямо сейчас — цельной копии не снять
        if (GameProcess.IsRunning(target.Profile))
        {
            Note(Loc.T("wcopy.gameRunning"));
            return;
        }
        StatusText = Loc.T("wcopy.copying");
        var (copied, failed) = await Task.Run(() => WorldCopies.Refresh(data));
        _copiedWorlds.AddRange(copied.Where(c => !_copiedWorlds.Contains(c)));
        if (copied.Count > 0) _main.RefreshWorldCopies(target.Profile);
        StatusText = "";
        foreach (var f in failed) Note(f);
    }

    /// <summary>
    /// Ставит один zip в профиль: старая версия — в хранилище, выключенный мод остаётся выключенным.
    /// interactive — спрашивать про ту же версию и даунгрейд (при обновлении/откате не спрашиваем: решение уже принято).
    /// </summary>
    private async Task<bool> InstallZipAsync(ModTarget target, string zip, bool interactive, List<string> installed, List<string> problems)
    {
        await CopyWorldsAsync(target, problems);
        try
        {
            // ставится строго в цель: свою папку или удалённый сервер (своё соединение), активный профиль не важен.
            // План приходит из фонового потока (после сканирования), а вопросы — окна: спрашиваем в потоке окна
            var outcome = await ModTargets.InstallAsync(target, zip, plan => Application.Current.Dispatcher.Invoke(() =>
            {
                var info = plan.Incoming.Info!;
                var old = plan.Replaces.FirstOrDefault()?.Info?.Version;
                if (interactive && plan.IsSameVersion && !Confirm(Loc.T("mods.reinstallConfirm", info.Name, info.Version))) return false;
                if (interactive && plan.IsDowngrade && !Confirm(Loc.T("mods.downgradeConfirm", info.Name, old, info.Version))) return false;
                // мод требует игру новее — спрашиваем всегда, и при обновлении: модбаза помечает релизы веткой (1.22.x),
                // а точное требование видно только в самом файле
                if (plan.NeedsGame is { } need)
                {
                    var question = old is null
                        ? Loc.T("mods.needsGameConfirm", info.Name, info.Version, need, target.Profile.Name, plan.Game)
                        : Loc.T("mods.needsGameUpdateConfirm", info.Name, info.Version, need, target.Profile.Name, plan.Game, old);
                    if (!Confirm(question))
                    {
                        problems.Add(Loc.T("mods.needsGameSkipped", info.Name, info.Version, need, target.Profile.Name));
                        return false;
                    }
                }
                if (target.IsRemote) StatusText = Loc.T("mods.uploading", info.Name, info.Version);
                return true;
            }));
            if (outcome is null) return false;
            installed.Add(outcome.Text);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException
                                       or HttpRequestException or TaskCanceledException)
        {
            problems.Add($"{Path.GetFileName(zip)}: {ex.Message}");
            return false;
        }
    }

    private async Task ReportAsync(string title, List<string> installed, List<string> problems)
    {
        await ReloadAsync();
        _ = FetchNewModsAsync();
        // зависимости — по итоговому состоянию, а не на момент установки каждого мода
        if (installed.Count > 0)
            problems.AddRange(DependencyIssues.Select(i => Loc.T("report.dependency", i.Describe())));
        StatusText = WithWorldCopies(installed.Count switch
        {
            0 => Loc.T("report.nothingChanged", title),
            <= 3 => $"{title}: " + string.Join("; ", installed),
            _ => Loc.T("report.changedCount", title, installed.Count), // длинный список — только число
        });
        if (problems.Count == 0) return;
        var text = (installed.Count > 0 ? $"{title}:\n• " + string.Join("\n• ", installed) + "\n\n" : "")
                   + Loc.T("report.attention") + ":\n• " + string.Join("\n• ", problems);
        MessageBox.Show(Application.Current.MainWindow, text, "eViSTool", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private bool ConfirmIfRunning(ModTarget target) =>
        !IsRunning(target)
        || Confirm(Loc.T("mods.runningContinue"));

    /// <summary>Скачать релиз и поставить его. Ошибки — в problems.</summary>
    private async Task<bool> DownloadAndInstallAsync(ModTarget target, string modId, string name, ModDbRelease release,
        List<string> installed, List<string> problems, Action<double>? onProgress = null, CancellationToken ct = default)
    {
        string? file = null;
        try
        {
            var progress = new Progress<double>(x =>
            {
                StatusText = Loc.T("dl.progress", name, release.ModVersion, x.ToString("P0"));
                onProgress?.Invoke(x);
            });
            file = await _updater.DownloadReleaseAsync(release, modId, progress, ct);
            return await InstallZipAsync(target, file, interactive: false, installed, problems);
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
    private void UpdateOne(ModRowViewModel? row)
    {
        if (row is not { CanUpdate: true } || CurrentTarget() is not { } target) return;
        Enqueue([QueueItemFor(target, row, row.Result.LatestCompatible!)]);
    }

    [RelayCommand]
    private void UpdateAll()
    {
        if (IsBusy && !IsQueueRunning) return;
        var todo = Rows.Where(r => r.CanUpdate).ToList();
        if (todo.Count == 0) return;

        var list = string.Join("\n", todo.Select(r => $"• {r.Name}: {r.Installed} → {r.Latest}"));
        if (CurrentTarget() is not { } target || !Confirm(Loc.T("mods.updateAllConfirm", todo.Count, list))) return;
        Enqueue(todo.Select(r => QueueItemFor(target, r, r.Result.LatestCompatible!)));
    }

    private static UpdateQueueItem QueueItemFor(ModTarget target, ModRowViewModel row, ModDbRelease release) =>
        new(target, row.ModId, row.Name, row.Installed, release.ModVersion ?? "?", release, null);

    // ---------- очередь установки ----------

    /// <summary>Очередь: обновления, откаты, выбранные версии. Ставятся по одному, можно добавлять на ходу.</summary>
    public ObservableCollection<UpdateQueueItem> Queue { get; } = [];
    [ObservableProperty] private bool _isQueueRunning;
    private CancellationTokenSource? _queueCts;

    public bool HasQueue => Queue.Count > 0;
    public string QueueTitle => Loc.T(IsQueueRunning ? "queue.titleRunning" : "queue.titleDone",
        Queue.Count(i => i.State is QueueState.Done or QueueState.Failed or QueueState.Cancelled), Queue.Count);
    public double QueueProgress => Queue.Count == 0 ? 0
        : (Queue.Count(i => i.State is QueueState.Done or QueueState.Failed or QueueState.Cancelled)
           + Queue.Where(i => i.IsWorking).Sum(i => i.Progress)) / Queue.Count;
    public bool HasQueueRetry => !IsQueueRunning && Queue.Any(i => i.State is QueueState.Failed or QueueState.Cancelled);

    /// <summary>Кнопки «Обновить» доступны и пока идёт очередь — пункт просто встанет в конец.</summary>
    public bool CanQueue => !IsBusy || IsQueueRunning;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanQueue));

    partial void OnIsQueueRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanQueue));
        NotifyQueue();
    }

    private void NotifyQueue()
    {
        OnPropertyChanged(nameof(HasQueue));
        OnPropertyChanged(nameof(QueueTitle));
        OnPropertyChanged(nameof(QueueProgress));
        OnPropertyChanged(nameof(HasQueueRetry));
    }

    /// <summary>
    /// Поставить в очередь; уже ждущий или ставящийся мод той же цели второй раз не добавляется.
    /// У каждого пункта своя цель: пункты разных профилей ставятся каждый в свой.
    /// </summary>
    public void Enqueue(IEnumerable<UpdateQueueItem> items)
    {
        if (IsBusy && !IsQueueRunning) return; // идёт другая операция (импорт модпака и т.п.)
        var adding = items.ToList();
        // игра или сервер цели запущены — спросить один раз на цель, пока ничего не начато
        var declined = adding.Select(i => i.Target).DistinctBy(t => t.ProfileId)
            .Where(t => !ConfirmIfRunning(t)).Select(t => t.ProfileId).ToHashSet();
        if (!IsQueueRunning)
        {
            // прошлый прогон закончен — его итоги убираем
            foreach (var old in Queue.Where(i => i.State is QueueState.Done).ToList()) Queue.Remove(old);
        }
        foreach (var item in adding.Where(i => !declined.Contains(i.Target.ProfileId)))
        {
            var same = Queue.FirstOrDefault(i => i.Target.ProfileId == item.Target.ProfileId
                                                 && string.Equals(i.ModId, item.ModId, StringComparison.OrdinalIgnoreCase));
            if (same is { State: QueueState.Waiting or QueueState.Working }) continue;
            if (same is not null) Queue.Remove(same);
            Queue.Add(item);
        }
        // пункты для разных профилей — у каждого видно, куда он ставится
        var several = Queue.Select(i => i.Target.ProfileId).Distinct().Count() > 1;
        foreach (var i in Queue) i.ShowTarget = several;
        NotifyQueue();
        if (!IsQueueRunning && Queue.Any(i => i.State == QueueState.Waiting)) _ = RunQueueAsync();
    }

    private async Task RunQueueAsync()
    {
        _queueCts = new CancellationTokenSource();
        var ct = _queueCts.Token;
        IsBusy = true;
        IsQueueRunning = true;
        var installed = new List<string>();
        try
        {
            while (!ct.IsCancellationRequested && Queue.FirstOrDefault(i => i.State == QueueState.Waiting) is { } item)
            {
                item.State = QueueState.Working;
                item.Progress = 0;
                NotifyQueue();
                var problems = new List<string>();
                bool ok;
                // каждый пункт — в свою цель, запомненную при добавлении
                if (item.Path is not null)
                    ok = await InstallZipAsync(item.Target, item.Path, interactive: false, installed, problems);
                else
                    ok = await DownloadAndInstallAsync(item.Target, item.ModId, item.Name, item.Release!, installed, problems,
                        x => { item.Progress = x; OnPropertyChanged(nameof(QueueProgress)); }, ct);

                item.State = ok ? QueueState.Done : ct.IsCancellationRequested ? QueueState.Cancelled : QueueState.Failed;
                item.Message = ok ? "" : ct.IsCancellationRequested ? "" : problems.FirstOrDefault() ?? "";
                if (ok && IsActive(item.Target)) await ReloadAsync(); // таблица и карточка сразу показывают новую версию
                NotifyQueue();
            }
            foreach (var i in Queue.Where(i => i.State == QueueState.Waiting)) i.State = QueueState.Cancelled;
        }
        finally
        {
            IsQueueRunning = false;
            IsBusy = false;
            _queueCts.Dispose();
            _queueCts = null;
        }

        await ReloadAsync();
        _ = FetchNewModsAsync();
        var done = Queue.Count(i => i.State == QueueState.Done);
        var failed = Queue.Count(i => i.State == QueueState.Failed);
        StatusText = Loc.T("queue.finished", done, failed);
    }

    [RelayCommand]
    private void StopQueue() => _queueCts?.Cancel();

    [RelayCommand]
    private void RetryQueue()
    {
        if (IsQueueRunning) return;
        // повтор — в ту же цель, что была у пункта
        foreach (var i in Queue.Where(i => i.State is QueueState.Failed or QueueState.Cancelled))
        {
            i.Message = "";
            i.State = QueueState.Waiting;
        }
        NotifyQueue();
        _ = RunQueueAsync();
    }

    [RelayCommand]
    private void CloseQueue()
    {
        if (IsQueueRunning) return;
        Queue.Clear();
        NotifyQueue();
    }

    // ---------- откат / другая версия ----------

    /// <summary>Версии для установки: сохранённые копии и релизы модбазы под версию игры.</summary>
    public async Task<List<VersionOption>> LoadVersionOptionsAsync(ModRowViewModel row, CancellationToken ct = default)
    {
        if (row.Local.Info is not { } info || Profile is not { } profile) return [];
        IReadOnlyList<ModDbRelease> releases = [];
        try
        {
            var remote = row.Result.Remote ?? await _db.GetModAsync(info.ModId, ct);
            if (remote is not null && profile.GameVersion is { } game)
                releases = UpdateChecker.CompatibleReleases(remote.Releases, game);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // без сети — только сохранённые копии
        }
        // прежние копии удалённого сервера лежат на той машине — отсюда предлагаем только релизы модбазы
        return RollbackWindow.BuildOptions(IsRemote ? null : ModBackupStore.ForProfile(profile.Profile), info.ModId, info.Version, releases);
    }

    /// <summary>Поставить выбранную версию (через очередь).</summary>
    public void InstallVersion(ModRowViewModel row, VersionOption option)
    {
        if (CurrentTarget() is { } target)
            Enqueue([new UpdateQueueItem(target, row.ModId, row.Name, row.Installed, option.Version, option.Release, option.Path)]);
    }

    [RelayCommand]
    private async Task RollbackAsync(ModRowViewModel? row)
    {
        if (row?.Local.Info is null || Profile is null || (IsBusy && !IsQueueRunning)) return;

        StatusText = Loc.T("mods.findingVersions", row.Name);
        var options = await LoadVersionOptionsAsync(row);
        StatusText = "";

        var dlg = new RollbackWindow(Loc.T("rollback.heading", row.Name, row.Installed), options)
        {
            Owner = Application.Current.MainWindow,
        };
        if (dlg.ShowDialog() == true && dlg.Selected is { } choice) InstallVersion(row, choice);
    }

    // ---------- зависимости ----------

    [RelayCommand]
    private async Task FixDependenciesAsync()
    {
        if (Profile is not { } start || IsBusy || DependencyIssues.Count == 0 || CurrentTarget() is not { } target) return;
        if (start.GameVersion is not { } game)
        {
            Error(Loc.T("mods.noGameVersion"));
            return;
        }

        var list = string.Join("\n", DependencyIssues.Select(i => "• " + i.Describe()));
        if (!Confirm(Loc.T("mods.fixDepsConfirm", list))) return;
        if (!ConfirmIfRunning(target)) return;

        var installed = new List<string>();
        var problems = new List<string>();
        IsBusy = true;
        try
        {
            await FixDependencyIssuesAsync(target, game, installed, problems);
        }
        finally
        {
            IsBusy = false;
        }
        await ReportAsync(Loc.T("report.dependencies"), installed, problems);
    }

    /// <summary>
    /// Докачивает недостающие/устаревшие зависимости и включает выключенные. Несколько кругов:
    /// у зависимостей бывают свои зависимости. Без вопросов — вызывающий уже спросил.
    /// </summary>
    private async Task FixDependencyIssuesAsync(ModTarget target, ModVersion game, List<string> installed, List<string> problems)
    {
        await CopyWorldsAsync(target, problems); // включение выключенных идёт мимо установки
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var round = 0; round < 5; round++)
            {
                // проблемы — по модам самой цели, а не того профиля, что открыт сейчас
                var (_, mods) = await ModTargets.ScanAsync(target);
                var issues = Dependencies.FindIssues(mods).Where(i => tried.Add(i.ModId)).ToList();
                if (issues.Count == 0) break;

                foreach (var issue in issues)
                {
                    if (issue.IsDisabled)
                    {
                        var mod = mods.First(l => string.Equals(l.Info?.ModId, issue.ModId, StringComparison.OrdinalIgnoreCase));
                        await ModTargets.SetEnabledAsync(target, mod, enabled: true);
                        installed.Add(Loc.T("mods.enabledShort", mod.Info!.Name));
                        continue;
                    }

                    StatusText = Loc.T("mods.searchingModDb", issue.ModId);
                    var found = await _updater.FindBestReleaseAsync(issue.ModId, game, _main.AllowUnstable, target.Profile.ToPolicy());
                    if (found is not { } f)
                    {
                        problems.Add(Loc.T("mods.depNotFound", issue.ModId, $"{game.Major}.{game.Minor}.x"));
                        continue;
                    }
                    await DownloadAndInstallAsync(target, issue.ModId, f.Mod.Name ?? issue.ModId, f.Release, installed, problems);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException
                                       or UnauthorizedAccessException)
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

    /// <summary>
    /// Скачать и поставить релиз из каталога — в текущий профиль и, если выбрали, ещё в другие (каждому свой релиз),
    /// затем предложить доставить зависимости: в каждом профиле, куда мод встал, — по одному общему вопросу.
    /// </summary>
    public async Task<bool> InstallFromCatalogAsync(string modId, string name, ModDbRelease release,
        IReadOnlyList<(ModTarget Target, ModDbRelease Release)>? also = null)
    {
        if (Profile is not { } profile || IsBusy || CurrentTarget() is not { } target) return false;
        if (!ConfirmIfRunning(target)) return false;
        var extra = (also ?? []).Where(a => ConfirmIfRunning(a.Target)).ToList(); // игра или сервер там запущены — спросить

        var installed = new List<string>();
        var problems = new List<string>();
        IsBusy = true;
        try
        {
            var done = new List<ModTarget>();
            foreach (var (t, r) in extra.Prepend((target, release)))
                if (await Labelled(t, t != target && extra.Count > 0, installed, problems,
                        () => DownloadAndInstallAsync(t, modId, name, r, installed, problems)))
                    done.Add(t);
            if (done.Count == 0)
            {
                await ReportAsync(Loc.T("report.install"), installed, problems);
                return false;
            }

            // зависимости — у тех целей, куда ставили, даже если профиль успели переключить
            var pending = new List<(ModTarget Target, ModVersion Game, IReadOnlyList<DependencyIssue> Issues)>();
            foreach (var t in done)
            {
                var (resolved, mods) = await ModTargets.ScanAsync(t);
                var issues = Dependencies.FindIssues(mods);
                if (issues.Count > 0 && (resolved.GameVersion ?? (t == target ? profile.GameVersion : null)) is { } game)
                    pending.Add((t, game, issues));
            }
            if (done.Any(IsActive)) await ReloadAsync();
            if (pending.Count > 0)
            {
                var list = pending.Count == 1 && pending[0].Target == target
                    ? string.Join("\n", pending[0].Issues.Select(i => "• " + i.Describe()))
                    : string.Join("\n\n", pending.Select(p => p.Target.Name + ":\n" + string.Join("\n", p.Issues.Select(i => "• " + i.Describe()))));
                if (Confirm(Loc.T("mods.installDepsConfirm", name, list)))
                    foreach (var (t, game, _) in pending)
                        await Labelled(t, pending.Count > 1 || t != target, installed, problems, async () =>
                        {
                            await FixDependencyIssuesAsync(t, game, installed, problems);
                            return true;
                        });
                if (done.Any(IsActive)) await ReloadAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
        await ReportAsync(Loc.T("report.installed"), installed, problems);
        return true;
    }

    /// <summary>Сделать шаг и, если ставим в несколько профилей, подписать его строки отчёта профилем: «Carry On 1.14 → Client C».</summary>
    private static async Task<bool> Labelled(ModTarget target, bool label, List<string> installed, List<string> problems, Func<Task<bool>> step)
    {
        var (i0, p0) = (installed.Count, problems.Count);
        var ok = await step();
        if (label)
        {
            for (var i = i0; i < installed.Count; i++) installed[i] += " → " + target.Name;
            for (var i = p0; i < problems.Count; i++) problems[i] = target.Name + ": " + problems[i];
        }
        return ok;
    }

    // ---------- модпаки ----------

    [RelayCommand]
    private async Task ExportPackAsync()
    {
        if (Profile is not { } profile || IsBusy || IsRemote) return;
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
        if (IsRemote)
        {
            Error(Loc.T("mods.remotePacksLater"));
            return;
        }

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
            if (CurrentTarget() is not { } target || !ConfirmIfRunning(target)) return;

            IsBusy = true;
            PackImportResult result;
            var copyProblems = new List<string>();
            try
            {
                await CopyWorldsAsync(target, copyProblems);
                var importer = new PackImporter(_db, _updater);
                var progress = new Progress<string>(s => StatusText = s);
                var (mirror, applyConfig) = (dlg.Mirror, dlg.ApplyConfig);
                // в фоне: иначе при паке «всё внутри» импорт идёт синхронно, окно подвисает,
                // а сообщения прогресса приходят уже после итога и затирают его
                // файлы с модбазы, перезалитые авторами модов, — ставить ли (спрашиваем до любых изменений профиля)
                bool AskChanged(IReadOnlyList<string> mods) => Application.Current.Dispatcher.Invoke(() =>
                    MessageBox.Show(Application.Current.MainWindow!, Loc.T("pack.askChanged", string.Join("\n", mods.Select(x => "• " + x))),
                        "eViSTool", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
                result = await Task.Run(() => importer.ApplyAsync(pack, plan, profile, mirror, applyConfig,
                    ModBackupStore.ForProfile(profile.Profile), progress, confirmChanged: AskChanged));
            }
            finally
            {
                IsBusy = false;
            }
            await ReportAsync(Loc.T("report.imported", pack.Manifest.Name), result.Done.ToList(), [.. copyProblems, .. result.Problems]);
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
    private void ShowFile(ModRowViewModel? row)
    {
        if (!IsRemote && row is not null && Path.Exists(row.FilePath)) Shell.ShowInFolder(row.FilePath);
    }

    [RelayCommand]
    private void OpenModsFolder()
    {
        if (!IsRemote && Profile?.InstallDir is { } dir && Directory.Exists(dir)) Shell.OpenFolder(dir);
    }

    private static bool Confirm(string text) =>
        MessageBox.Show(Application.Current.MainWindow, text, "eViSTool", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static void Error(string text) =>
        MessageBox.Show(Application.Current.MainWindow, text, "eViSTool", MessageBoxButton.OK, MessageBoxImage.Warning);
}

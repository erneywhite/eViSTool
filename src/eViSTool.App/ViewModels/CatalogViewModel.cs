using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Versioning;
using eViSTool.Core.Localization;

namespace eViSTool.App.ViewModels;

public sealed record Choice<T>(string Title, T Value)
{
    public override string ToString() => Title;
}

/// <summary>Вкладка «Каталог»: поиск по модбазе и установка одной кнопкой.</summary>
public sealed partial class CatalogViewModel : ObservableObject
{

    private readonly MainViewModel _main;
    private readonly ModDbClient _db;
    private readonly CatalogService _catalog;
    private readonly DispatcherTimer _searchDelay;
    private bool _loaded;
    private CancellationTokenSource? _detailsCts;

    [ObservableProperty] private IReadOnlyList<CatalogItemViewModel> _results = [];
    [ObservableProperty] private CatalogItemViewModel? _selected;
    [ObservableProperty] private ModDetailsViewModel? _details;
    [ObservableProperty] private string _search = "";
    /// <summary>Теги; значение null — «все теги» (подпись переводится, значение — нет).</summary>
    [ObservableProperty] private IReadOnlyList<Choice<string?>> _tags = [];
    [ObservableProperty] private Choice<string?>? _selectedTag;
    [ObservableProperty] private Choice<string?> _selectedSide = null!;
    [ObservableProperty] private Choice<CatalogSort> _selectedSort = null!;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isLoading;

    /// <summary>Фильтр по версии игры: null — любая, иначе ветка "1.22".</summary>
    [ObservableProperty] private IReadOnlyList<Choice<string?>> _branches = [];
    [ObservableProperty] private Choice<string?>? _selectedBranch;

    /// <summary>Моды, у которых есть релиз для ветки активного профиля — для пометки в списке.</summary>
    private IReadOnlySet<long>? _profileCompat;
    private string? ProfileBranch => _main.ActiveProfile?.Resolved.GameVersion is { } g ? $"{g.Major}.{g.Minor}" : null;
    private int _searchGeneration;

    /// <summary>Ширина карточки мода (разделитель можно тянуть, ширина запоминается).</summary>
    public double DetailsWidth
    {
        get => _main.Layout.CatalogDetailsWidth;
        set { _main.Layout.CatalogDetailsWidth = Math.Max(320, value); OnPropertyChanged(); }
    }

    public void SaveLayout() => _main.SaveSettings();

    [ObservableProperty] private IReadOnlyList<Choice<string?>> _sides = [];

    /// <summary>Фильтр «установлен ли в активном профиле»: 0 — все, 1 — установленные, 2 — не установленные.</summary>
    [ObservableProperty] private IReadOnlyList<Choice<int>> _installFilters = [];
    [ObservableProperty] private Choice<int> _selectedInstallFilter = null!;

    /// <summary>Подзаголовок страницы: для какого профиля и версии игры.</summary>
    public string HeaderSubtitle => _main.ActiveProfile is { } p
        ? Loc.T("catalog.subtitle", p.Name, p.GameVersionText)
        : "";
    [ObservableProperty] private IReadOnlyList<Choice<CatalogSort>> _sorts = [];

    /// <summary>Списки фильтров с переведёнными подписями; выбор сохраняется по значению.</summary>
    private void BuildChoices()
    {
        var side = SelectedSide?.Value;
        var sort = SelectedSort?.Value ?? CatalogSort.Trending;
        var branch = SelectedBranch?.Value;
        var tag = SelectedTag?.Value;
        var install = SelectedInstallFilter?.Value ?? 0;

        Sides =
        [
            new(Loc.T("catalog.sideAny"), null),
            new(Loc.T("catalog.sideClient"), "client"),
            new(Loc.T("catalog.sideServer"), "server"),
        ];
        Sorts =
        [
            new(Loc.T("catalog.sortTrending"), CatalogSort.Trending),
            new(Loc.T("catalog.sortDownloads"), CatalogSort.Downloads),
            new(Loc.T("catalog.sortFollows"), CatalogSort.Follows),
            new(Loc.T("catalog.sortRecent"), CatalogSort.RecentlyUpdated),
            new(Loc.T("catalog.sortName"), CatalogSort.Name),
        ];
        Branches = [new(Loc.T("catalog.anyVersion"), null), .. _catalog.Branches.Select(b =>
            new Choice<string?>(Loc.T(b == ProfileBranch ? "catalog.hasVersionMine" : "catalog.hasVersion", b), b))];
        Tags = [new(Loc.T("catalog.allTags"), null), .. _catalog.Tags().Select(t => new Choice<string?>(t, t))];
        InstallFilters =
        [
            new(Loc.T("catalog.installAll"), 0),
            new(Loc.T("catalog.installYes"), 1),
            new(Loc.T("catalog.installNo"), 2),
        ];
        _selectedInstallFilter = InstallFilters.First(f => f.Value == install);
        OnPropertyChanged(nameof(SelectedInstallFilter));

        _selectedSide = Sides.First(s => s.Value == side);
        _selectedSort = Sorts.First(s => s.Value == sort);
        _selectedBranch = Branches.FirstOrDefault(b => b.Value == branch) ?? Branches[0];
        _selectedTag = Tags.FirstOrDefault(t => t.Value == tag) ?? Tags[0];
        OnPropertyChanged(nameof(SelectedSide));
        OnPropertyChanged(nameof(SelectedSort));
        OnPropertyChanged(nameof(SelectedBranch));
        OnPropertyChanged(nameof(SelectedTag));
    }

    /// <summary>Сменился язык — перестроить подписи и строки списка.</summary>
    public void OnLanguageChanged()
    {
        BuildChoices();
        if (_loaded) ApplySearch();
        else StatusText = "";
        if (Selected is { } s) OnSelectedChanged(s); // карточка собрана на старом языке — пересобрать
    }

    public CatalogViewModel(MainViewModel main, ModDbClient db)
    {
        _main = main;
        _db = db;
        _catalog = new CatalogService(db);
        BuildChoices();

        // поиск не на каждую букву, а после короткой паузы
        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); ApplySearch(); };

        // поставили/удалили мод — обновить отметки «установлен»
        main.Mods.LocalModsChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HeaderSubtitle)); // перечитывают и при смене профиля
            if (!_loaded) return;
            ApplySearch();
            Details?.RefreshInstalled(main.Mods.InstalledVersions);
        };
    }

    partial void OnSearchChanged(string value) { _searchDelay.Stop(); _searchDelay.Start(); }
    partial void OnSelectedTagChanged(Choice<string?>? value) { if (value is not null) ApplySearch(); }
    partial void OnSelectedSideChanged(Choice<string?> value) { if (value is not null) ApplySearch(); }
    partial void OnSelectedSortChanged(Choice<CatalogSort> value) { if (value is not null) ApplySearch(); }
    partial void OnSelectedBranchChanged(Choice<string?>? value) { if (value is not null) ApplySearch(); }
    partial void OnSelectedInstallFilterChanged(Choice<int> value) { if (value is not null) ApplySearch(); }

    /// <summary>Первое открытие вкладки — загрузить каталог (из кэша, если свежий).</summary>
    public Task EnsureLoadedAsync()
    {
        if (_loaded) return Task.CompletedTask;
        return _loadTask ??= LoadAsync(force: false); // повторный вызов ждёт ту же загрузку
    }

    private Task? _loadTask;

    /// <summary>
    /// Открыть мод в каталоге (из вкладки «Моды»): фильтры сбрасываются, чтобы мод точно был виден,
    /// в поиск подставляется его название, мод выбирается и открывается карточка.
    /// </summary>
    public async Task<bool> ShowModAsync(long? assetId, string? modId, string? name)
    {
        await EnsureLoadedAsync();
        if (!_loaded) return false;

        var item = (assetId is > 0 ? _catalog.Items.FirstOrDefault(i => i.AssetId == assetId) : null)
                   ?? (modId is null ? null : _catalog.Items.FirstOrDefault(i =>
                       i.ModIdStrs.Any(s => string.Equals(s, modId, StringComparison.OrdinalIgnoreCase))))
                   ?? (name is null ? null : _catalog.Items.FirstOrDefault(i =>
                       string.Equals(i.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (item is null)
        {
            StatusText = Loc.T("catalog.notInCatalog", name ?? modId);
            return false;
        }

        SelectedBranch = Branches[0];
        SelectedTag = Tags[0];
        SelectedSide = Sides[0];
        SelectedInstallFilter = InstallFilters[0];
        Search = item.Name ?? "";
        _searchDelay.Stop();
        await ApplySearchAsync();
        Selected = Results.FirstOrDefault(r => r.Item.AssetId == item.AssetId);
        return Selected is not null;
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync(force: true);

    private async Task LoadAsync(bool force)
    {
        IsLoading = true;
        StatusText = Loc.T("catalog.loading");
        try
        {
            await _catalog.LoadAsync(force);
            await _catalog.LoadBranchesAsync(force);
            if (ProfileBranch is { } mine && _catalog.Branches.Contains(mine))
                _profileCompat = await _catalog.CompatibleAssetsAsync(mine, force);

            var keepBranch = SelectedBranch?.Value ?? (_loaded ? null : ProfileBranch);
            BuildChoices();
            _loaded = true;
            // по умолчанию — ветка игры активного профиля
            SelectedBranch = Branches.FirstOrDefault(b => b.Value == keepBranch) ?? Branches[0];
            ApplySearch();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            StatusText = Loc.T("catalog.loadFailed", ex.Message);
        }
        finally
        {
            IsLoading = false;
            if (!_loaded) _loadTask = null; // не загрузилось — следующая попытка начнёт заново
        }
    }

    // ошибки поиска не должны теряться молча в фоновой задаче
    private void ApplySearch() => ApplySearchAsync().ContinueWith(t => App.WriteCrashLog(t.Exception),
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private async Task ApplySearchAsync()
    {
        if (!_loaded) return;
        var generation = ++_searchGeneration;

        IReadOnlySet<long>? only = null;
        if (SelectedBranch?.Value is { } branch)
        {
            try
            {
                StatusText = Loc.T("catalog.matchingBranch", branch);
                only = await _catalog.CompatibleAssetsAsync(branch);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                StatusText = Loc.T("catalog.branchFailed", branch, ex.Message);
                return;
            }
            if (generation != _searchGeneration) return; // пока ждали, условия поменялись
        }

        var found = _catalog.Search(new CatalogQuery
        {
            Text = Search,
            Tag = SelectedTag?.Value,
            Side = SelectedSide?.Value,
            Sort = SelectedSort?.Value ?? CatalogSort.Trending,
            OnlyAssets = only,
        });

        var installed = _main.Mods.InstalledVersions;
        var keep = Selected?.Item.ModId;
        var branchLabel = ProfileBranch is { } pb ? $"{pb}.x" : null;
        Results = found.Select(i => new CatalogItemViewModel(i, installed,
            _profileCompat is null || branchLabel is null ? null : _profileCompat.Contains(i.AssetId), branchLabel))
            .Where(r => (SelectedInstallFilter?.Value ?? 0) switch { 1 => r.IsInstalled, 2 => !r.IsInstalled, _ => true })
            .ToList();
        if (keep is not null)
        {
            var again = Results.FirstOrDefault(r => r.Item.ModId == keep);
            if (again is not null) SetProperty(ref _selected, again, nameof(Selected)); // без перезагрузки карточки
            else Selected = null;
        }

        var at = _catalog.LoadedAt?.ToLocalTime().ToString("dd.MM HH:mm") ?? "—";
        StatusText = Loc.T("catalog.found", Results.Count, _catalog.Items.Count, at);
    }

    partial void OnSelectedChanged(CatalogItemViewModel? value)
    {
        _detailsCts?.Cancel();
        if (value is null)
        {
            Details = null;
            return;
        }
        Details = new ModDetailsViewModel(value.Item, _main.ActiveProfile?.Resolved.GameVersion, _main.Mods.InstalledVersions, this);
        _detailsCts = new CancellationTokenSource();
        _ = Details.LoadAsync(_db, _detailsCts.Token);
    }

    /// <summary>Установка из карточки (последний совместимый релиз или выбранный).</summary>
    public async Task InstallAsync(ModDetailsViewModel details, ModDbRelease release)
    {
        var id = details.ModId ?? release.ModIdStr;
        if (string.IsNullOrEmpty(id))
        {
            MessageBox.Show(Application.Current.MainWindow, Loc.T("catalog.noModId"), "eViSTool");
            return;
        }
        await _main.Mods.InstallFromCatalogAsync(id, details.Name, release);
    }
}

/// <summary>Строка списка каталога.</summary>
public sealed class CatalogItemViewModel(ModDbListItem item, IReadOnlyDictionary<string, string> installed,
    bool? compatible = null, string? branch = null)
{
    /// <summary>Пометка «нет версии для 1.22.x» — если известно, что релиза под ветку игры нет.</summary>
    public string? NoVersionText { get; } = compatible == false ? Loc.T("catalog.noVersionFor", branch) : null;

    public ModDbListItem Item { get; } = item;
    public string Name { get; } = item.Name ?? item.PrimaryModId ?? "?";
    public string Author { get; } = item.Author ?? "";
    public string Summary { get; } = item.Summary ?? "";
    public string? Logo { get; } = string.IsNullOrWhiteSpace(item.Logo) ? null : item.Logo;
    public string Stats { get; } = $"⬇ {item.Downloads:N0}   ♥ {item.Follows:N0}";
    public string Initials { get; } = ModRowViewModel.MakeInitials(item.Name ?? item.PrimaryModId ?? "?");
    public string Tag { get; } = item.Tags.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "";
    public string? InstalledVersion { get; } = item.PrimaryModId is { } id && installed.TryGetValue(id, out var v) ? v : null;
    public bool IsInstalled => InstalledVersion is not null;
    public string InstalledText => IsInstalled ? Loc.T("catalog.installedBadge", InstalledVersion) : Loc.T("catalog.availableBadge");
}

/// <summary>Карточка мода: описание, совместимость, установка.</summary>
public sealed partial class ModDetailsViewModel : ObservableObject
{
    private readonly ModDbListItem _item;
    private readonly ModVersion? _game;
    private readonly CatalogViewModel _owner;

    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _compatibility = Loc.T("card.loadingReleases");
    [ObservableProperty] private bool _canInstall;
    [ObservableProperty] private string _installText = Loc.T("card.install", "");
    [ObservableProperty] private string? _installedVersion;
    [ObservableProperty] private IReadOnlyList<string> _screenshots = [];
    [ObservableProperty] private ModDbMod? _mod;
    [ObservableProperty] private ModDbRelease? _bestRelease;

    /// <summary>Вкладка «Версии» (иначе «Обзор»).</summary>
    [ObservableProperty] private bool _isVersionsTab;
    [ObservableProperty] private IReadOnlyList<VersionRowViewModel> _versions = [];
    [ObservableProperty] private string _versionsStatus = "";

    public string Name { get; }
    public string? ModId { get; }
    public string Author { get; }
    public string? Logo { get; }
    public string Stats { get; }
    public string Side { get; }
    public string TagsText { get; }
    public string PageUrl { get; }
    public string Initials { get; }
    public string Tag { get; }

    public ModDetailsViewModel(ModDbListItem item, ModVersion? game, IReadOnlyDictionary<string, string> installed, CatalogViewModel owner)
    {
        _item = item;
        _game = game;
        _owner = owner;
        Name = item.Name ?? item.PrimaryModId ?? "?";
        ModId = item.PrimaryModId;
        Author = item.Author is { Length: > 0 } a ? Loc.T("card.author", a) : "";
        Logo = string.IsNullOrWhiteSpace(item.Logo) ? null : item.Logo;
        Stats = Loc.T("card.stats", item.Downloads.ToString("N0"), item.Follows.ToString("N0"), item.LastReleased?[..10]);
        Side = item.Side switch
        {
            "client" => Loc.T("card.sideClient"),
            "server" => Loc.T("card.sideServer"),
            _ => Loc.T("card.sideBoth"),
        };
        TagsText = string.Join(", ", item.Tags.Where(t => !string.IsNullOrWhiteSpace(t)));
        Tag = item.Tags.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "";
        Initials = ModRowViewModel.MakeInitials(Name);
        PageUrl = item.PageUrl;
        Description = item.Summary ?? "";
        RefreshInstalled(installed);
    }

    public void RefreshInstalled(IReadOnlyDictionary<string, string> installed)
    {
        InstalledVersion = ModId is not null && installed.TryGetValue(ModId, out var v) ? v : null;
        UpdateInstallText();
        BuildVersions();
    }

    public async Task LoadAsync(ModDbClient db, CancellationToken ct)
    {
        try
        {
            var mod = await db.GetModAsync(_item.ModId.ToString(), ct);
            if (ct.IsCancellationRequested) return;
            Mod = mod;
            if (mod is null)
            {
                Compatibility = Loc.T("card.notFound");
                return;
            }

            Description = Html.ToPlainText(mod.Text) ?? Description;
            Screenshots = mod.Screenshots.Select(s => s.MainFile).Where(u => !string.IsNullOrWhiteSpace(u)).ToList()!;

            if (_game is null)
            {
                Compatibility = Loc.T("mods.noGameVersion");
                BestRelease = UpdateChecker.PickLatest(mod.Releases, allowUnstable: true);
            }
            else
            {
                var branch = $"{_game.Major}.{_game.Minor}.x";
                // как во вкладке «Моды»: стоит пре-релиз — значит, сознательно на нестабильной ветке мода
                var onPrerelease = ModVersion.ParseOrNull(InstalledVersion)?.IsPrerelease == true;
                var stable = UpdateChecker.PickLatestCompatible(mod.Releases, _game, allowUnstable: onPrerelease);
                BestRelease = stable ?? UpdateChecker.PickLatestCompatible(mod.Releases, _game, allowUnstable: true);
                var latest = UpdateChecker.PickLatest(mod.Releases, allowUnstable: true);
                Compatibility = BestRelease is not null
                    ? "✔ " + Loc.T("card.hasVersion", BestRelease.ModVersion, branch)
                      + (ModVersion.ParseOrNull(BestRelease.ModVersion)?.IsPrerelease == true ? " " + Loc.T("card.prerelease") : "")
                    : "✖ " + Loc.T("card.noVersion", branch)
                      + (latest is not null ? ". " + Loc.T("card.latestFor", latest.ModVersion, latest.GameVersions.LastOrDefault()) : "");
            }
            UpdateInstallText();
            BuildVersions();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (!ct.IsCancellationRequested) Compatibility = Loc.T("card.loadFailed", ex.Message);
            if (!ct.IsCancellationRequested) VersionsStatus = Loc.T("card.loadFailed", ex.Message);
        }
    }

    private void UpdateInstallText()
    {
        var target = BestRelease?.ModVersion;
        var cmp = ModVersion.ParseOrNull(InstalledVersion) is { } have && ModVersion.ParseOrNull(target) is { } want
            ? have.CompareTo(want) : (int?)null;
        InstallText = InstalledVersion is null ? Loc.T("card.install", target)
            : cmp == 0 ? Loc.T("card.reinstall", target)
            : cmp > 0 ? Loc.T("card.newerInstalled", InstalledVersion)
            : Loc.T("card.update", InstalledVersion, target);
        // установленную более новую версию одной кнопкой не откатываем — для этого «Другая версия…»
        CanInstall = BestRelease?.MainFile is not null && cmp is null or <= 0;
    }

    [RelayCommand]
    private async Task Install()
    {
        if (BestRelease is not null) await _owner.InstallAsync(this, BestRelease);
    }

    /// <summary>Релизы для вкладки «Версии»: под версию игры профиля (если она известна), новые сверху.</summary>
    private void BuildVersions()
    {
        if (Mod is null)
        {
            VersionsStatus = Loc.T("mcard.versionsLoading");
            return;
        }
        var releases = _game is null ? Mod.Releases : UpdateChecker.CompatibleReleases(Mod.Releases, _game);
        Versions = RollbackWindow.BuildOptions(null, ModId ?? "", InstalledVersion, releases)
            .Select(o => new VersionRowViewModel(o, InstalledVersion ?? "")).ToList();
        VersionsStatus = Versions.Count == 0
            ? Loc.T("catalog.versionsNone")
            : _game is null ? "" : Loc.T("catalog.versionsHint", $"{_game.Major}.{_game.Minor}.x");
    }

    [RelayCommand]
    private async Task InstallVersion(VersionRowViewModel? v)
    {
        if (v?.Option.Release is not { } release) return;
        var text = InstalledVersion is null
            ? Loc.T("catalog.installVersionConfirm", Name, v.Version)
            : Loc.T("mcard.installVersionConfirm", Name, v.Version, InstalledVersion);
        if (MessageBox.Show(Application.Current.MainWindow, text, "eViSTool", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await _owner.InstallAsync(this, release);
    }

    [RelayCommand]
    private void OpenPage() => Shell.OpenUrl(PageUrl);

    [RelayCommand]
    private void OpenScreenshot(string? url)
    {
        if (string.IsNullOrEmpty(url) || Screenshots.Count == 0) return;
        var index = Math.Max(0, Screenshots.ToList().IndexOf(url));
        new ScreenshotWindow(Screenshots, index, Loc.T("shots.title", Name)) { Owner = Application.Current.MainWindow }.ShowDialog();
    }
}

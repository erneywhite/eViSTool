using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Versioning;

namespace eViSTool.App.ViewModels;

public sealed record Choice<T>(string Title, T Value)
{
    public override string ToString() => Title;
}

/// <summary>Вкладка «Каталог»: поиск по модбазе и установка одной кнопкой.</summary>
public sealed partial class CatalogViewModel : ObservableObject
{
    private const string AllTags = "Все теги";

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
    [ObservableProperty] private IReadOnlyList<string> _tags = [AllTags];
    [ObservableProperty] private string _selectedTag = AllTags;
    [ObservableProperty] private Choice<string?> _selectedSide;
    [ObservableProperty] private Choice<CatalogSort> _selectedSort;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isLoading;

    /// <summary>Фильтр по версии игры: null — любая, иначе ветка "1.22".</summary>
    [ObservableProperty] private IReadOnlyList<Choice<string?>> _branches = [new("Любая версия игры", null)];
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

    public IReadOnlyList<Choice<string?>> Sides { get; } =
    [
        new("Любая сторона", null),
        new("Нужен на клиенте", "client"),
        new("Нужен на сервере", "server"),
    ];

    public IReadOnlyList<Choice<CatalogSort>> Sorts { get; } =
    [
        new("Популярные сейчас", CatalogSort.Trending),
        new("Больше скачиваний", CatalogSort.Downloads),
        new("Больше подписчиков", CatalogSort.Follows),
        new("Недавно обновлённые", CatalogSort.RecentlyUpdated),
        new("По названию", CatalogSort.Name),
    ];

    public CatalogViewModel(MainViewModel main, ModDbClient db)
    {
        _main = main;
        _db = db;
        _catalog = new CatalogService(db);
        _selectedSide = Sides[0];
        _selectedSort = Sorts[0];

        // поиск не на каждую букву, а после короткой паузы
        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); ApplySearch(); };

        // поставили/удалили мод — обновить отметки «установлен»
        main.Mods.LocalModsChanged += (_, _) =>
        {
            if (!_loaded) return;
            ApplySearch();
            Details?.RefreshInstalled(main.Mods.InstalledVersions);
        };
    }

    partial void OnSearchChanged(string value) { _searchDelay.Stop(); _searchDelay.Start(); }
    partial void OnSelectedTagChanged(string value) => ApplySearch();
    partial void OnSelectedSideChanged(Choice<string?> value) => ApplySearch();
    partial void OnSelectedSortChanged(Choice<CatalogSort> value) => ApplySearch();
    partial void OnSelectedBranchChanged(Choice<string?>? value) => ApplySearch();

    /// <summary>Первое открытие вкладки — загрузить каталог (из кэша, если свежий).</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loaded || IsLoading) return;
        await LoadAsync(force: false);
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync(force: true);

    private async Task LoadAsync(bool force)
    {
        IsLoading = true;
        StatusText = "Загрузка каталога модбазы…";
        try
        {
            await _catalog.LoadAsync(force);
            await _catalog.LoadBranchesAsync(force);
            if (ProfileBranch is { } mine && _catalog.Branches.Contains(mine))
                _profileCompat = await _catalog.CompatibleAssetsAsync(mine, force);

            var keepBranch = SelectedBranch?.Value ?? (_loaded ? null : ProfileBranch);
            Branches = [new("Любая версия игры", null), .. _catalog.Branches.Select(b =>
                new Choice<string?>(b == ProfileBranch ? $"Есть версия для {b}.x (твоя)" : $"Есть версия для {b}.x", b))];
            _loaded = true;
            Tags = [AllTags, .. _catalog.Tags()];
            // по умолчанию — ветка игры активного профиля
            SelectedBranch = Branches.FirstOrDefault(b => b.Value == keepBranch) ?? Branches[0];
            ApplySearch();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            StatusText = $"Не удалось загрузить каталог: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async void ApplySearch()
    {
        if (!_loaded) return;
        var generation = ++_searchGeneration;

        IReadOnlySet<long>? only = null;
        if (SelectedBranch?.Value is { } branch)
        {
            try
            {
                StatusText = $"Сверяю моды с версией {branch}.x…";
                only = await _catalog.CompatibleAssetsAsync(branch);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                StatusText = $"Не удалось получить моды для {branch}.x: {ex.Message}";
                return;
            }
            if (generation != _searchGeneration) return; // пока ждали, условия поменялись
        }

        var found = _catalog.Search(new CatalogQuery
        {
            Text = Search,
            Tag = SelectedTag == AllTags ? null : SelectedTag,
            Side = SelectedSide.Value,
            Sort = SelectedSort.Value,
            OnlyAssets = only,
        });

        var installed = _main.Mods.InstalledVersions;
        var keep = Selected?.Item.ModId;
        var branchLabel = ProfileBranch is { } pb ? $"{pb}.x" : null;
        Results = found.Select(i => new CatalogItemViewModel(i, installed,
            _profileCompat is null || branchLabel is null ? null : _profileCompat.Contains(i.AssetId), branchLabel)).ToList();
        if (keep is not null) Selected = Results.FirstOrDefault(r => r.Item.ModId == keep);

        var at = _catalog.LoadedAt?.ToLocalTime().ToString("dd.MM HH:mm") ?? "—";
        StatusText = $"Найдено: {Results.Count} из {_catalog.Items.Count}   ·   каталог от {at}";
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
            MessageBox.Show(Application.Current.MainWindow, "У мода в модбазе не указан modid — поставь его вручную с сайта.", "eViSTool");
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
    public string? NoVersionText { get; } = compatible == false ? $"нет версии для {branch}" : null;

    public ModDbListItem Item { get; } = item;
    public string Name { get; } = item.Name ?? item.PrimaryModId ?? "?";
    public string Author { get; } = item.Author ?? "";
    public string Summary { get; } = item.Summary ?? "";
    public string? Logo { get; } = string.IsNullOrWhiteSpace(item.Logo) ? null : item.Logo;
    public string Stats { get; } = $"⬇ {item.Downloads:N0}   ♥ {item.Follows:N0}";
    public string? InstalledVersion { get; } = item.PrimaryModId is { } id && installed.TryGetValue(id, out var v) ? v : null;
    public bool IsInstalled => InstalledVersion is not null;
    public string InstalledText => IsInstalled ? $"установлен {InstalledVersion}" : "";
}

/// <summary>Карточка мода: описание, совместимость, установка.</summary>
public sealed partial class ModDetailsViewModel : ObservableObject
{
    private readonly ModDbListItem _item;
    private readonly ModVersion? _game;
    private readonly CatalogViewModel _owner;

    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _compatibility = "Загружаю сведения о релизах…";
    [ObservableProperty] private bool _canInstall;
    [ObservableProperty] private string _installText = "Установить";
    [ObservableProperty] private string? _installedVersion;
    [ObservableProperty] private IReadOnlyList<string> _screenshots = [];
    [ObservableProperty] private ModDbMod? _mod;
    [ObservableProperty] private ModDbRelease? _bestRelease;

    public string Name { get; }
    public string? ModId { get; }
    public string Author { get; }
    public string? Logo { get; }
    public string Stats { get; }
    public string Side { get; }
    public string TagsText { get; }
    public string PageUrl { get; }

    public ModDetailsViewModel(ModDbListItem item, ModVersion? game, IReadOnlyDictionary<string, string> installed, CatalogViewModel owner)
    {
        _item = item;
        _game = game;
        _owner = owner;
        Name = item.Name ?? item.PrimaryModId ?? "?";
        ModId = item.PrimaryModId;
        Author = item.Author is { Length: > 0 } a ? $"автор: {a}" : "";
        Logo = string.IsNullOrWhiteSpace(item.Logo) ? null : item.Logo;
        Stats = $"⬇ {item.Downloads:N0} скачиваний   ♥ {item.Follows:N0}   обновлён {item.LastReleased?[..10]}";
        Side = item.Side switch
        {
            "client" => "только клиент",
            "server" => "только сервер",
            _ => "клиент и сервер",
        };
        TagsText = string.Join(", ", item.Tags.Where(t => !string.IsNullOrWhiteSpace(t)));
        PageUrl = item.PageUrl;
        Description = item.Summary ?? "";
        RefreshInstalled(installed);
    }

    public void RefreshInstalled(IReadOnlyDictionary<string, string> installed)
    {
        InstalledVersion = ModId is not null && installed.TryGetValue(ModId, out var v) ? v : null;
        UpdateInstallText();
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
                Compatibility = "Мод не найден в модбазе";
                return;
            }

            Description = Html.ToPlainText(mod.Text) ?? Description;
            Screenshots = mod.Screenshots.Select(s => s.MainFile).Where(u => !string.IsNullOrWhiteSpace(u)).ToList()!;

            if (_game is null)
            {
                Compatibility = "Версия игры не определена — укажи папку игры в настройках профиля";
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
                    ? $"✔ Есть версия {BestRelease.ModVersion} для {branch}{(ModVersion.ParseOrNull(BestRelease.ModVersion)?.IsPrerelease == true ? " (пре-релиз)" : "")}"
                    : $"✖ Нет версии для {branch}" + (latest is not null ? $". Последняя — {latest.ModVersion} (для {latest.GameVersions.LastOrDefault()})" : "");
            }
            UpdateInstallText();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (!ct.IsCancellationRequested) Compatibility = $"Не удалось загрузить сведения: {ex.Message}";
        }
    }

    private void UpdateInstallText()
    {
        var target = BestRelease?.ModVersion;
        var cmp = ModVersion.ParseOrNull(InstalledVersion) is { } have && ModVersion.ParseOrNull(target) is { } want
            ? have.CompareTo(want) : (int?)null;
        InstallText = InstalledVersion is null ? $"Установить {target}"
            : cmp == 0 ? $"Переустановить {target}"
            : cmp > 0 ? $"Установлена новее ({InstalledVersion})"
            : $"Обновить {InstalledVersion} → {target}";
        // установленную более новую версию одной кнопкой не откатываем — для этого «Другая версия…»
        CanInstall = BestRelease?.MainFile is not null && cmp is null or <= 0;
    }

    [RelayCommand]
    private async Task Install()
    {
        if (BestRelease is not null) await _owner.InstallAsync(this, BestRelease);
    }

    [RelayCommand]
    private async Task ChooseVersion()
    {
        if (Mod is null) return;
        var releases = _game is null ? Mod.Releases : UpdateChecker.CompatibleReleases(Mod.Releases, _game);
        var options = RollbackWindow.BuildOptions(null, ModId ?? "", InstalledVersion, releases);
        var dlg = new RollbackWindow($"{Name} — выбор версии", options) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() == true && dlg.Selected?.Release is { } release)
            await _owner.InstallAsync(this, release);
    }

    [RelayCommand]
    private void OpenPage() => Shell.OpenUrl(PageUrl);

    [RelayCommand]
    private void OpenScreenshot(string? url)
    {
        if (string.IsNullOrEmpty(url) || Screenshots.Count == 0) return;
        var index = Math.Max(0, Screenshots.ToList().IndexOf(url));
        new ScreenshotWindow(Screenshots, index, $"{Name} — скриншоты") { Owner = Application.Current.MainWindow }.ShowDialog();
    }
}

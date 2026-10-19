using eViSTool.Presentation;
using eViSTool.Core.Profiles;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Versioning;
using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

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
    private readonly IUiTimer _searchDelay;
    private bool _loaded;
    private CancellationTokenSource? _detailsCts;

    [ObservableProperty] private IReadOnlyList<CatalogItemViewModel> _results = [];
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ClearSelectionCommand))] private CatalogItemViewModel? _selected;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ClearSelectionCommand))] private ModDetailsViewModel? _details;
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
    // версия игры — та же, что у вкладки «Моды» (у удалённого профиля — от его агента)
    private string? ProfileBranch => _main.Mods.GameVersion is { } g ? $"{g.Major}.{g.Minor}" : null;
    private string? _branchSeen;
    private int _searchGeneration;

    /// <summary>Ширина карточки мода (разделитель можно тянуть, ширина запоминается).</summary>
    public double DetailsWidth
    {
        get => _main.Layout.CatalogDetailsWidth;
        set { _main.Layout.CatalogDetailsWidth = Math.Max(280, value); OnPropertyChanged(); } // 280 — MinWidth колонки в окне
    }

    public void SaveLayout() => _main.SaveSettings();

    [ObservableProperty] private IReadOnlyList<Choice<string?>> _sides = [];

    /// <summary>Фильтр «установлен ли в активном профиле»: 0 — все, 1 — установленные, 2 — не установленные.</summary>
    [ObservableProperty] private IReadOnlyList<Choice<int>> _installFilters = [];
    [ObservableProperty] private Choice<int> _selectedInstallFilter = null!;

    /// <summary>Подзаголовок страницы: для какого профиля и версии игры.</summary>
    public string HeaderSubtitle => _main.ActiveProfile is { } p
        ? Loc.T("catalog.subtitle", p.Name, _main.Mods.GameVersion?.ToString() ?? p.GameVersionText)
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
        // фильтры из настроек ставим в поля напрямую, минуя свойства: их обработчики перезапускали бы поиск
        // на каждом присваивании; окно узнаёт о значениях из OnPropertyChanged
#pragma warning disable MVVMTK0034
        _selectedInstallFilter = InstallFilters.First(f => f.Value == install);
        OnPropertyChanged(nameof(SelectedInstallFilter));

        _selectedSide = Sides.First(s => s.Value == side);
        _selectedSort = Sorts.First(s => s.Value == sort);
        _selectedBranch = Branches.FirstOrDefault(b => b.Value == branch) ?? Branches[0];
        _selectedTag = Tags.FirstOrDefault(t => t.Value == tag) ?? Tags[0];
#pragma warning restore MVVMTK0034
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
        _searchDelay = Ui.Timer(TimeSpan.FromMilliseconds(250));
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); ApplySearch(); };

        // поставили/удалили мод — обновить отметки «установлен»
        main.Mods.LocalModsChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HeaderSubtitle)); // перечитывают и при смене профиля
            // открытая карточка — под версию игры профиля, который активен сейчас (сменили профиль, агент сообщил версию)
            Details?.Retarget(main.Mods.GameVersion);
            if (!_loaded) return;
            if (ProfileBranch != _branchSeen) _ = OnBranchChangedAsync();
            else ApplySearch();
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

        // пустой modid или имя (мод без modinfo) совпал бы с любым модом каталога, у которого в списке modid есть ""
        if (string.IsNullOrWhiteSpace(modId)) modId = null;
        if (string.IsNullOrWhiteSpace(name)) name = null;
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
            _branchSeen = ProfileBranch;
            _profileCompat = ProfileBranch is { } mine && _catalog.Branches.Contains(mine)
                ? await _catalog.CompatibleAssetsAsync(mine, force) : null;

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

    /// <summary>Сменилась ветка игры активного профиля: пометки «нет версии для …» и подписи фильтра — под новую.</summary>
    private async Task OnBranchChangedAsync()
    {
        _branchSeen = ProfileBranch;
        try
        {
            _profileCompat = ProfileBranch is { } mine && _catalog.Branches.Contains(mine)
                ? await _catalog.CompatibleAssetsAsync(mine) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _profileCompat = null; // пометок не будет — лучше, чем пометки от чужой версии
        }
        var branch = SelectedBranch?.Value;
        BuildChoices();
#pragma warning disable MVVMTK0034
        _selectedBranch = Branches.FirstOrDefault(b => b.Value == branch) ?? Branches[0];
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(SelectedBranch));
        ApplySearch();
    }

    // ошибки поиска не должны теряться молча в фоновой задаче
    private void ApplySearch() => ApplySearchAsync().ContinueWith(t => Ui.Platform.WriteCrashLog(t.Exception!),
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
        _rebuilding = keep is not null;
        Results = found.Select(i => new CatalogItemViewModel(i, installed,
            _profileCompat is null || branchLabel is null ? null : _profileCompat.Contains(i.AssetId), branchLabel))
            .Where(r => (SelectedInstallFilter?.Value ?? 0) switch { 1 => r.IsInstalled, 2 => !r.IsInstalled, _ => true })
            .ToList();
        _rebuilding = false;
        if (keep is not null)
        {
            var again = Results.FirstOrDefault(r => r.Item.ModId == keep);
#pragma warning disable MVVMTK0034 // намеренно мимо свойства: его обработчик перезагрузил бы карточку
            if (again is not null) SetProperty(ref _selected, again, nameof(Selected)); // без перезагрузки карточки
#pragma warning restore MVVMTK0034
            else
            {
                Selected = null;
                Details = null; // мод выпал из списка (фильтр «Не установленные» и т.п.) — карточку закрываем явно
            }
        }

        var at = (_catalog.LoadedAt is { } loaded ? Dates.DayMonthClock(loaded.ToLocalTime()) : "—");
        StatusText = Loc.T("catalog.found", Results.Count, _catalog.Items.Count, at);
    }

    // новый список результатов на миг сбрасывает выбор в нём — карточку при этом не закрываем:
    // тот же мод выбирается снова ниже, и карточка остаётся (иначе после установки она пропадала насовсем)
    private bool _rebuilding;

    partial void OnSelectedChanged(CatalogItemViewModel? value)
    {
        if (value is null && _rebuilding) return;
        _detailsCts?.Cancel();
        if (value is null)
        {
            Details = null;
            return;
        }
        Details = new ModDetailsViewModel(value.Item, _main.Mods.GameVersion, _main.Mods.InstalledVersions, this);
        _detailsCts = new CancellationTokenSource();
        _ = Details.LoadAsync(_db, _detailsCts.Token);
    }

    /// <summary>Снять выбор мода (Esc и кнопка «Назад» мыши): плитка больше не выбрана, справа вместо карточки — «Выбери мод».</summary>
    [RelayCommand(CanExecute = nameof(CanClearSelection))]
    private void ClearSelection()
    {
        Selected = null; // карточку закрывает OnSelectedChanged
        Details = null;
    }

    private bool CanClearSelection() => Selected is not null || Details is not null;

    /// <summary>Установка из карточки (последний совместимый релиз или выбранный).</summary>
    public async Task InstallAsync(ModDetailsViewModel details, ModDbRelease release)
    {
        var id = details.ModId ?? release.ModIdStr;
        if (string.IsNullOrEmpty(id))
        {
            Ui.Info(Loc.T("catalog.noModId"));
            return;
        }
        // клиентский мод выделенному серверу ни к чему (не загружает): спросить. Серверный у игрока нужен одиночной игре
        var kind = _main.ActiveProfile?.Model.Kind ?? ProfileKind.Client;
        var side = ModSides.Parse(details.Mod?.Side ?? details.RawSide);
        if (ModSides.IsUnneeded(side, kind)
            && !Ui.Confirm(Loc.T("catalog.clientOnlyAsk", details.Name)))
            return;
        // релиз сверяется с версией игры профиля, куда сейчас поставим (её же запомнит установка как цель)
        var game = _main.Mods.GameVersion;
        if (!CatalogPick.Fits(release, game)
            && !Ui.Confirm(game is null
                       ? Loc.T("catalog.unknownGameAsk", details.Name, release.ModVersion)
                       : Loc.T("catalog.otherGameAsk", details.Name, release.ModVersion,
                           string.Join(", ", release.GameVersions.TakeLast(3)), game), byDefault: Answer.No, warning: true))
            return;

        // мод нужен и в других профилях (сервер ↔ клиент) — предложить поставить заодно, каждому свою версию
        var also = await PickAlsoAsync(details, id, release);
        if (also is null) return; // отменили
        await _main.Mods.InstallFromCatalogAsync(id, details.Name, release, [.. also.Select(o => (o.Target, o.Release!))]);
    }

    /// <summary>
    /// Окно «Установить также в». Пустой список — ставить только сюда (предлагать некуда или не о чем), null — отмена.
    /// </summary>
    private async Task<IReadOnlyList<AlsoInstallOption>?> PickAlsoAsync(ModDetailsViewModel details, string id, ModDbRelease release)
    {
        if (details.Mod is not { } mod || _main.ActiveProfile is not { } active) return [];
        var side = ModSides.Parse(mod.Side ?? details.RawSide);
        if (!AlsoInstall.WorthOffering(side)) return [];
        var others = _main.Profiles
            .Where(p => p != active && AlsoInstall.Needed(side, p.Model.Kind))
            .Select(p => (Profile: p, Target: ModTarget.For(p.Model)))
            .Where(x => x.Target is not null)
            .ToList();
        if (others.Count == 0) return [];

        StatusText = Loc.T("also.checking");
        // удалённый сервер не на связи — не ждать вечно; сервер у хостера читается дольше
        using var cts = new CancellationTokenSource(others.Max(x => ModTargets.ScanTimeout(x.Target!)));
        var options = await Task.WhenAll(others.Select(x => AlsoInstall.EvaluateAsync(x.Target!, id, mod.Releases, release, cts.Token)));
        StatusText = "";
        var game = AlsoInstall.GameText(_main.Mods.GameVersion);
        var chosen = AlsoInstallPicker.Choose(_main, active, Loc.T("also.hint", details.Name),
            Loc.T("also.thisProfile") + " · " + Loc.T("also.willInstall", game, release.ModVersion),
            [.. options.Select((o, i) => new AlsoChoice(others[i].Profile, o, o.Describe(), o.CanInstall,
                o.State == AlsoInstallState.AlreadyThere, active.Model.IsLinkedTo(others[i].Profile.Model)))]);
        return chosen?.Cast<AlsoInstallOption>().ToList();
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
    public string? Logo { get; } = ModDbLogo.Usable(item.Logo); // стандартная картинка модбазы — не логотип: вместо неё буквы
    public string Stats { get; } = $"⬇ {item.Downloads:N0}   ♥ {item.Follows:N0}";
    public string Initials { get; } = ModRowViewModel.MakeInitials(item.Name ?? item.PrimaryModId ?? "?");

    /// <summary>Оттенок значка с буквами — тот же, что у этого мода в «Моих модах» (по modid).</summary>
    public int Hue { get; } = ModTint.Hue(item.PrimaryModId ?? item.Name);
    public string Tag { get; } = item.Tags.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "";
    public string? InstalledVersion { get; } = item.PrimaryModId is { } id && installed.TryGetValue(id, out var v) ? v : null;
    public bool IsInstalled => InstalledVersion is not null;
    public string InstalledText => IsInstalled ? Loc.T("catalog.installedBadge", InstalledVersion) : Loc.T("catalog.availableBadge");
}

/// <summary>Карточка мода: описание, совместимость, установка.</summary>
public sealed partial class ModDetailsViewModel : ObservableObject
{
    private readonly ModDbListItem _item;
    private ModVersion? _game; // версия игры профиля, под которую подобран релиз; меняется вместе с профилем
    private readonly CatalogViewModel _owner;

    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _compatibility = Loc.T("card.loadingReleases");

    /// <summary>Есть релиз под версию игры профиля (зелёным) — или известно, что его нет (красноватым), как в «Моих модах».</summary>
    [ObservableProperty] private bool _isCompatible;
    [ObservableProperty] private bool _isIncompatible;
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

    /// <summary>Дата последнего релиза на модбазе (ГГГГ-ММ-ДД); нет — пусто, строка «Обновлён» не видна.</summary>
    public string Updated { get; }
    public string Side { get; }

    /// <summary>Сторона, как её пишет модбаза («both» / «client» / «server»).</summary>
    public string? RawSide => _item.Side;
    public string TagsText { get; }
    public string PageUrl { get; }
    public string Initials { get; }

    /// <summary>Оттенок шапки с буквами, когда логотипа нет (<see cref="ModTint"/>).</summary>
    public int Hue { get; }
    public string Tag { get; }

    public ModDetailsViewModel(ModDbListItem item, ModVersion? game, IReadOnlyDictionary<string, string> installed, CatalogViewModel owner)
    {
        _item = item;
        _game = game;
        _owner = owner;
        Name = item.Name ?? item.PrimaryModId ?? "?";
        ModId = item.PrimaryModId;
        Author = item.Author is { Length: > 0 } a ? Loc.T("card.author", a) : "";
        Logo = ModDbLogo.Usable(item.Logo);
        Stats = Loc.T("card.stats", item.Downloads.ToString("N0"), item.Follows.ToString("N0"));
        Updated = item.LastReleased is { Length: >= 10 } released ? released[..10] : "";
        Side = item.Side switch
        {
            "client" => Loc.T("card.sideClient"),
            "server" => Loc.T("card.sideServer"),
            _ => Loc.T("card.sideBoth"),
        };
        TagsText = string.Join(", ", item.Tags.Where(t => !string.IsNullOrWhiteSpace(t)));
        Tag = item.Tags.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "";
        Initials = ModRowViewModel.MakeInitials(Name);
        Hue = ModTint.Hue(item.PrimaryModId ?? item.Name);
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

    /// <summary>Активный профиль (или его версия игры) сменился — подобрать релиз и список версий под новую.</summary>
    public void Retarget(ModVersion? game)
    {
        if (Equals(game?.ToString(), _game?.ToString())) return;
        _game = game;
        if (Mod is null) return; // релизы ещё грузятся — подберутся под новую версию сами
        Evaluate(Mod);
        UpdateInstallText();
        BuildVersions();
    }

    /// <summary>Лучший релиз и строка совместимости — под версию игры профиля (см. CatalogPick).</summary>
    private void Evaluate(ModDbMod mod)
    {
        var onPrerelease = ModVersion.ParseOrNull(InstalledVersion)?.IsPrerelease == true;
        var choice = CatalogPick.Best(mod.Releases, _game, onPrerelease);
        BestRelease = choice.Release;
        var branch = _game is { } g ? $"{g.Major}.{g.Minor}.x" : "";
        IsIncompatible = choice.Fit == CatalogFit.NoneForBranch;
        IsCompatible = choice.Fit is CatalogFit.Compatible or CatalogFit.CompatiblePrerelease;
        Compatibility = choice.Fit switch
        {
            CatalogFit.UnknownGame => Loc.T("card.unknownGame") + (choice.Release is { } r ? " " + Loc.T("card.latestStable", r.ModVersion) : ""),
            CatalogFit.NoneForBranch => "✖ " + Loc.T("card.noVersion", branch)
                                        + (choice.Latest is { } latest ? ". " + Loc.T("card.latestFor", latest.ModVersion, latest.GameVersions.LastOrDefault()) : ""),
            _ => "✔ " + Loc.T("card.hasVersion", choice.Release!.ModVersion, branch)
                 + (choice.Fit == CatalogFit.CompatiblePrerelease ? " " + Loc.T("card.prerelease") : ""),
        };
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

            Evaluate(mod);
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
        Versions = VersionOptions.Build(null, ModId ?? "", InstalledVersion, releases)
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
        if (!Ui.Confirm(text))
            return;
        await _owner.InstallAsync(this, release);
    }

    [RelayCommand]
    private void OpenPage() => Ui.OpenUrl(PageUrl);

    [RelayCommand]
    private void OpenScreenshot(string? url)
    {
        if (string.IsNullOrEmpty(url) || Screenshots.Count == 0) return;
        var index = Math.Max(0, Screenshots.ToList().IndexOf(url));
        AppWindows.Current.ShowScreenshots(Screenshots, index, Loc.T("shots.title", Name));
    }
}

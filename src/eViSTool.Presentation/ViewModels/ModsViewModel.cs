using eViSTool.Presentation;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Versioning;
using eViSTool.Core.Localization;
using eViSTool.Core.Packs;
using eViSTool.Core.Server;
using eViSTool.Presentation.ViewModels.Mods;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Чипы над таблицей модов.</summary>
public enum ModFilter { All, Updates, Problems, Pinned, Disabled, Unneeded, History }

/// <summary>
/// Вкладка «Мои моды»: к ней привязано окно — таблица модов открытого профиля с чипами и поиском, карточка выбранного,
/// очередь установки, история. Сама вкладка держит то, что видно, и команды; работу делают её части:
/// откуда берутся моды — <see cref="LocalModSource"/>, <see cref="RemoteModSource"/> и <see cref="HostedModSource"/>, установка в цель —
/// <see cref="ModInstallService"/>, установка сразу (архивы, каталог, зависимости) — <see cref="ModInstallActions"/>,
/// очередь — <see cref="ModInstallQueue"/>, модпаки — <see cref="ModPackActions"/>, код набора — <see cref="ModCodeActions"/>,
/// откат по истории — <see cref="ModHistoryActions"/>, видимость и порядок строк таблицы — <see cref="ModListRules"/>.
/// </summary>
public sealed partial class ModsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ModUpdateService _service;
    private readonly ModDbClient _db;

    private readonly ModInstallService _installer;
    private readonly ModInstallActions _installs;
    private readonly ModInstallQueue _queue;
    private readonly LocalModSource _disk;
    private readonly RemoteModSource _agent;
    private readonly HostedModSource _hosted;
    private readonly ModPackActions _packs;
    private readonly ModCodeActions _codes;
    private readonly ModHistoryActions _historyActions;
    private readonly HostedWorldBusy _worldJob;

    /// <summary>Последний ответ модбазы (modid → что там есть): после локальных операций статусы не теряются.</summary>
    internal Dictionary<string, ModDbResult> ModDbAnswers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Логотипы, которые узнали карточки модов до проверки обновлений (modid → адрес): строка таблицы показывает тот же
    /// логотип, что шапка карточки, и не теряет его, когда таблицу строят заново. В <see cref="ModDbAnswers"/> их не
    /// кладём: тот считается полным ответом проверки (по нему досверяются новые моды и собирается модпак), а здесь
    /// известны моды поштучно. Логотип мода от профиля не зависит — при смене профиля память остаётся.
    /// </summary>
    private readonly Dictionary<string, string> _cardLogos = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Моды открытого профиля: с диска этой машины, со слов агента удалённого сервера или по FTP у хостера.</summary>
    internal IReadOnlyList<LocalMod> Locals { get; set; } = [];

    public ObservableCollection<ModRowViewModel> Rows { get; } = [];
    public FilteredList<ModRowViewModel> View { get; }

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ClearSelectionCommand))] private ModFilter _filter = ModFilter.All;
    [ObservableProperty] private string _search = "";

    /// <summary>Сортировка: 0 — сначала важное, 1 — по имени.</summary>
    [ObservableProperty] private int _sortIndex;

    /// <summary>Выбранный в таблице мод и его карточка справа.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanChangeSelected)), NotifyCanExecuteChangedFor(nameof(ClearSelectionCommand))]
    private ModRowViewModel? _selected;

    /// <summary>Пункты контекстного меню, которые меняют мод: у мода хостера недоступны.</summary>
    public bool CanChangeSelected => Selected is not { CanChange: false };
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ClearSelectionCommand))] private ModCardViewModel? _card;
    private CancellationTokenSource? _cardLoad;

    /// <summary>Таблицу строят заново (<see cref="Rebuild"/>): выбор, который таблица меняет сама, карточку не трогает.</summary>
    private bool _rebuilding;

    /// <summary>
    /// Файлы модов, чей переключатель щёлкнули (<see cref="ToggleEnabledAsync"/>): при ближайшей перестройке их строки —
    /// новые, даже если мод не поменялся. Щелчок сам перекидывает переключатель, а привязка к прежней строке значение не
    /// повторяет: отказались, запись не удалась — переключатель так и показывал бы несделанное.
    /// </summary>
    private readonly HashSet<string> _renew = new(StringComparer.Ordinal);

    /// <summary>
    /// Снять выбор мода (Esc и кнопка «Назад» мыши): строка в таблице больше не выбрана, справа вместо карточки снова
    /// сводка профиля. В «Истории» правая панель — про сеанс, а не про мод: там снимать нечего, а спрятанный выбор
    /// таблицы снимать незаметно не надо.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanClearSelection))]
    private void ClearSelection()
    {
        // пустой выбор сам карточку не убирает (так таблицу перестраивают после операции, см. OnSelectedChanged) — убрать
        // её явно; без карточки перестройка таблицы и выбор не вернёт
        _cardLoad?.Cancel();
        Card = null;
        Selected = null;
    }

    private bool CanClearSelection() => !IsHistory && (Selected is not null || Card is not null);

    // счётчики над таблицей и на чипах
    [ObservableProperty] private int _updatesAvailable;
    [ObservableProperty] private int _problemCount;
    [ObservableProperty] private int _pinnedCount;
    [ObservableProperty] private int _disabledCount;

    /// <summary>Клиентские моды в серверном профиле — фильтр виден, только когда они есть.</summary>
    [ObservableProperty] private int _unneededCount;
    public bool HasUnneeded => UnneededCount > 0;
    partial void OnUnneededCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasUnneeded));
        OnPropertyChanged(nameof(UnneededLabel));
    }

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
    public string PinnedLabel => Loc.Plural("mods.cntPinned", PinnedCount);
    public string UnneededLabel => Loc.Plural("mods.cntUnneeded", UnneededCount);
    public string ShownText => Loc.T("mods.shown", View.Count, Rows.Count);

    public IReadOnlyList<string> SortModes { get; } = [Loc.T("mods.sortImportant"), Loc.T("mods.sortName")];

    // ---------- сводка профиля: правая панель, пока мод не выбран ----------

    /// <summary>Когда обновления этого профиля сверяли с модбазой; null — после открытия профиля ещё не сверяли.</summary>
    [ObservableProperty] private DateTime? _checkedAt;

    /// <summary>Идёт проверка обновлений: в колонке статуса — мерцающие заглушки вместо статусов.</summary>
    public bool IsChecking => CheckCommand.IsRunning;

    /// <summary>Есть что обновить: главная кнопка страницы — «Обновить всё», а не «Проверить обновления» в сводке.</summary>
    public bool HasUpdates => UpdateCount > 0;

    /// <summary>Заголовок сводки: «Стенд · сервер».</summary>
    public string SummaryTitle => _main.ActiveProfile is { } p ? $"{p.Name} · {p.KindText}" : Loc.T("mods.noProfile");

    /// <summary>«3 из 4» — сколько модов включено из всех (числа те же, что на вкладках-счётчиках).</summary>
    public string SummaryEnabledText => ModCount == 0 ? Loc.T("mods.summaryNoMods") : Loc.T("mods.summaryOf", ModCount - DisabledCount, ModCount);

    /// <summary>Число модов с проблемами (как на вкладке «проблем») или «нет».</summary>
    public string SummaryProblemsText => ProblemCount == 0 ? Loc.T("mods.summaryNone") : ProblemCount.ToString(Loc.Culture);

    public bool HasProblems => ProblemCount > 0;

    /// <summary>«проверены сегодня, 14:05» или «ещё не проверялись».</summary>
    public string SummaryCheckedText => CheckedAt is { } at ? Loc.T("mods.summaryCheckedAt", Dates.Relative(at)) : Loc.T("mods.summaryNotChecked");

    partial void OnCheckedAtChanged(DateTime? value) => OnPropertyChanged(nameof(SummaryCheckedText));

    /// <summary>Сменился язык: подписи счётчиков сами пересчитываются только при смене числа.</summary>
    public void OnLanguageChanged()
    {
        // выпадающий список запоминает текст выбранного пункта — выбрать его заново, чтобы подпись сменилась
        var sort = SortIndex;
        SortIndex = -1;
        SortIndex = sort;
        foreach (var name in new[] { nameof(InstalledLabel), nameof(UpdatesLabel), nameof(ProblemsLabel), nameof(DisabledLabel),
                     nameof(PinnedLabel), nameof(UnneededLabel), nameof(ShownText), nameof(UpdateAllText), nameof(DependencyText),
                     nameof(SummaryTitle), nameof(SummaryEnabledText), nameof(SummaryProblemsText), nameof(SummaryCheckedText) })
            OnPropertyChanged(name);
        WorldJobText = _worldJob.Text;
        RefreshSaidJob();
    }

    /// <summary>Ширина карточки (тянется разделителем, запоминается). Нижняя граница — как минимум колонки в окне (280):
    /// иначе оставленная разделителем ширина 280–340 запомнилась бы как 340, и карточка прыгнула бы шире.</summary>
    public double CardWidth
    {
        get => _main.Layout.ModsCardWidth;
        set { _main.Layout.ModsCardWidth = Math.Max(CardMinWidth, value); OnPropertyChanged(); }
    }

    /// <summary>Самая узкая карточка «Моих модов» — она же MinWidth её колонки в окне.</summary>
    public const double CardMinWidth = 280;

    public void SaveLayout() => _main.SaveSettings();

    partial void OnModCountChanged(int value)
    {
        OnPropertyChanged(nameof(InstalledLabel));
        OnPropertyChanged(nameof(SummaryEnabledText));
    }

    partial void OnUpdatesAvailableChanged(int value) => OnPropertyChanged(nameof(UpdatesLabel));

    partial void OnProblemCountChanged(int value)
    {
        OnPropertyChanged(nameof(ProblemsLabel));
        OnPropertyChanged(nameof(SummaryProblemsText));
        OnPropertyChanged(nameof(HasProblems));
    }

    partial void OnDisabledCountChanged(int value)
    {
        OnPropertyChanged(nameof(DisabledLabel));
        OnPropertyChanged(nameof(SummaryEnabledText));
    }

    partial void OnPinnedCountChanged(int value) => OnPropertyChanged(nameof(PinnedLabel));

    partial void OnSelectedChanged(ModRowViewModel? value)
    {
        // null приходит и когда таблицу перестраивают после операции — тогда карточку не трогаем,
        // её заменит та же строка из новой таблицы. Пока строки встают на места, таблица может на миг выбрать соседнюю
        // (выбранную строку заменили) — её карточку не строим: выбор вернёт Rebuild. Таблица вернула выбор той же строке
        // (перечитала свой вид, GridOrder) — карточка и так её
        if (value is null || _rebuilding || ReferenceEquals(value, Card?.Row)) return;
        ShowCard(value);
    }

    /// <summary>Карточка мода справа — новая, для строки <paramref name="value"/>.</summary>
    private void ShowCard(ModRowViewModel value)
    {
        _cardLoad?.Cancel();
        _cardLoad = new CancellationTokenSource();
        // тот же мод после операции — остаёмся на той же вкладке карточки, и новая карточка берёт у прежней то, что та
        // уже узнала (страницу с модбазы, списки): иначе карточка мигала бы после каждого переключения. Вкладка — до того,
        // как карточку увидит окно: иначе на миг вставал бы «Обзор», и «Версии» проявлялись бы заново
        var keepTab = Card is { IsVersionsTab: true } old && old.Row.ModId == value.ModId;
        var card = new ModCardViewModel(value, this, Profile?.GameVersion, _main.ActiveProfile?.Name ?? "", Card);
        if (keepTab) card.IsVersionsTab = true;
        Card = card;
        _ = card.LoadAsync(_db, _cardLoad.Token);
    }

    /// <summary>
    /// Карточка сама сходила на модбазу (мод ещё не проверяли) и узнала логотип — он встаёт и в строку таблицы: у всех
    /// строк с этим modid (дубликаты тоже) и у тех, что построятся заново после операции.
    /// </summary>
    internal void LogoLearned(string modId, string logo)
    {
        if (modId.Length == 0) return;
        _cardLogos[modId] = logo;
        foreach (var row in Rows)
            if (string.Equals(row.ModId, modId, StringComparison.OrdinalIgnoreCase)) row.UseLogo(logo);
    }

    [RelayCommand]
    private void SetFilter(ModFilter filter) => Filter = filter;

    partial void OnDependencyIssuesChanged(IReadOnlyList<DependencyIssue> value)
    {
        OnPropertyChanged(nameof(HasDependencyIssues));
        OnPropertyChanged(nameof(DependencyText));
    }

    partial void OnUpdateCountChanged(int value)
    {
        OnPropertyChanged(nameof(UpdateAllText));
        OnPropertyChanged(nameof(HasUpdates));
    }

    public ModsViewModel(MainViewModel main, ModDbClient db)
    {
        _main = main;
        _db = db;
        _service = new ModUpdateService(db);
        var updater = new ModUpdater(db);
        _installer = new ModInstallService(main, updater, s => StatusText = s, () => GameVersion);
        _queue = new ModInstallQueue(new QueueHost(this));
        _queue.PropertyChanging += OnQueueChanging;
        _queue.PropertyChanged += OnQueueChanged;
        _disk = new LocalModSource(this, main);
        _agent = new RemoteModSource(this, main);
        _hosted = new HostedModSource(this, main);
        _packs = new ModPackActions(this, _installer, _service, db, updater);
        _installs = new ModInstallActions(this, _installer, _packs);
        _codes = new ModCodeActions(this, _packs, _service, db);
        _historyActions = new ModHistoryActions(this, _installer, db);
        // главной модели, собранной в тесте без конструктора, операций с копиями мира нет — тогда и следить не за чем
        _worldJob = new HostedWorldBusy(() => _main.HostedWorldJobs);
        _worldJob.TextChanged += () => WorldJobText = _worldJob.Text;
        _worldJob.BusyChanged += OnWorldJobBusyChanged;
        View = new FilteredList<ModRowViewModel>(Rows);
        View.Filter = r => ModListRules.Matches(r, Filter, Search);
        ApplySort();
        CheckCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning)) OnPropertyChanged(nameof(IsChecking));
        };
    }

    /// <summary>Вкладка «Мои моды» открыта — следим за папками модов (закрыта — не тратим на это время).</summary>
    public void SetActive(bool active)
    {
        _disk.SetActive(active);
        _hosted.SetActive(active);
        // «проверены сегодня, 23:40» после полуночи — уже «вчера»: пересчитать при заходе на вкладку и в полночь, пока она открыта
        if (active) OnPropertyChanged(nameof(SummaryCheckedText));
        if (active) ArmMidnight();
        else _midnight?.Stop();
    }

    private IUiTimer? _midnight;

    private void ArmMidnight()
    {
        if (_midnight is null)
        {
            _midnight = Ui.Timer(TimeSpan.FromHours(1));
            _midnight.Tick += (_, _) =>
            {
                OnPropertyChanged(nameof(SummaryCheckedText));
                ArmMidnight(); // при переводе часов сработает на час раньше или позже — тогда просто ещё раз до полуночи
            };
        }
        _midnight.Stop();
        _midnight.Interval = DateTime.Today.AddDays(1) - DateTime.Now + TimeSpan.FromSeconds(1);
        _midnight.Start();
    }

    /// <summary>Сторож оповещений серверов у хостера: ему — что окно поставило на сервер («Обновлены моды»).</summary>
    internal HostedWatch? HostedWatch => _main.HostedWatch;

    /// <summary>Источник модов сервера у хостера — тесты сверяют с ним часы и опрос.</summary>
    internal HostedModSource HostedSource => _hosted;

    /// <summary>Один опрос «поменяли ли моды на сервере со стороны» — то, что делает таймер вкладки раз в 30 с.</summary>
    internal Task PollHostedAsync() => _hosted.PollAsync();

    partial void OnFilterChanged(ModFilter value)
    {
        RefreshView();
        OnPropertyChanged(nameof(IsHistory));
        if (value == ModFilter.History) _ = LoadHistoryAsync();
    }

    partial void OnSearchChanged(string value)
    {
        RefreshView();
        History.SetSearch(value);
    }

    // ---------- история изменений модов ----------

    /// <summary>Вкладка «История»: вместо таблицы — сеансы изменений, справа — детали выбранного.</summary>
    public ModHistoryViewModel History { get; } = new();
    public bool IsHistory => Filter == ModFilter.History;

    private async Task LoadHistoryAsync()
    {
        if (CurrentTarget() is not { } target) return;
        await History.LoadAsync(target, Locals);
    }

    [RelayCommand]
    private Task RollbackSessionAsync(HistorySessionRow? session) =>
        session is null ? Task.CompletedTask : _historyActions.RevertAsync(session, [.. session.Changes.Where(c => c.CanRevert)]);

    [RelayCommand]
    private Task RevertChangeAsync(HistoryChangeRow? change) =>
        change is null || History.Selected is not { } session ? Task.CompletedTask : _historyActions.RevertAsync(session, [change]);

    /// <summary>
    /// Последние изменения одного мода — для вкладки «Версии» его карточки: «сегодня, 20:37 · 1.2.10 → 1.2.11 · «Обновить всё»».
    /// История не читается (нет связи, старый агент) — пусто, карточке это не мешает.
    /// </summary>
    public Task<IReadOnlyList<string>> ModHistoryLinesAsync(string modId, int count = 30) => _historyActions.LinesAsync(modId, count);

    partial void OnSortIndexChanged(int value)
    {
        if (value >= 0) ApplySort(); // -1 — пункт перевыбирают при смене языка, порядок тот же
    }

    private void RefreshView()
    {
        View.Refresh();
        OnPropertyChanged(nameof(ShownText));
    }

    // по названию — с учётом языка системы; «Важность» (проблемы, обновления…) — сначала
    private void ApplySort() => View.Sort = ModListRules.Order(SortIndex);

    // ---------- профиль и откуда берутся моды ----------

    /// <summary>Моды берутся с диска этой машины, у агента удалённого сервера по сети или по FTP у хостера.</summary>
    private IModSource Source => IsHosted ? _hosted : IsRemote ? _agent : _disk;

    /// <summary>Профиль открытых модов: папки и версия игры (у удалённого — со слов агента, пути — на той машине).</summary>
    internal ResolvedProfile? Profile => Source.Resolved;

    /// <summary>
    /// Версия игры активного профиля — одна на всё окно (каталог берёт её отсюда): у своего — по папке игры,
    /// у удалённого — та, что сообщил его агент. null — неизвестна (агент ещё не ответил, папка игры не найдена).
    /// </summary>
    public ModVersion? GameVersion => Profile?.GameVersion;

    /// <summary>Открыт удалённый сервер: список модов и папки — со слов агента, пути — на той машине.</summary>
    internal bool IsRemote => _main.ActiveProfile?.Model.IsRemote == true;

    /// <summary>Профиль на этой машине: папку модов можно открыть, модпаки — собрать и поставить.</summary>
    public bool IsLocalProfile => _main.ActiveProfile?.Model.IsLocal ?? true;

    /// <summary>Открыт сервер у хостера: моды — по FTP.</summary>
    public bool IsHosted => _main.ActiveProfile?.Model.IsHosted == true;

    /// <summary>У сервера у хостера не определилась версия игры: плашка «укажи её в «Профилях»».</summary>
    public bool HostedVersionMissing => IsHosted && _hosted.Resolved is { GameVersion: null };

    public string HostedVersionMissingText => Loc.T("hosted.versionMissing");

    /// <summary>Перейти к настройкам профиля в «Профилях» (подключение, версия игры сервера у хостера).</summary>
    [RelayCommand]
    private void OpenHostedSettings() => _main.OpenProfileEditor(_main.ActiveProfile);

    /// <summary>
    /// Цель для действия, начатого сейчас: снимок активного профиля. Дальше операция работает только с ней —
    /// переключение профиля посреди загрузки её не сдвинет (раньше мод мог уйти в прежний профиль).
    /// </summary>
    internal ModTarget? CurrentTarget()
    {
        if (_main.ActiveProfile is not { } profile) return null;
        if (ModTarget.For(profile.Model) is { } target) return target;
        Error(ModTarget.UnreadableText(profile.Model));
        return null;
    }

    /// <summary>Подзаголовок страницы: «Мой мир · клиент / Vintage Story 1.22.7».</summary>
    public string HeaderSubtitle => _main.ActiveProfile is { } p
        ? Loc.T("mods.subtitle", p.Name, p.KindText, Profile?.GameVersion?.ToString() ?? Loc.T("common.notFound"))
        : Loc.T("mods.noProfile");

    /// <summary>У своего профиля не нашлась игра: в «Моих модах» плашка с кнопкой «Указать папку».</summary>
    public bool GameMissing => IsLocalProfile && _main.ActiveProfile is { } p && p.Resolved.GameVersion is null;

    public string GameMissingText => Loc.T("mods.gameMissing", _main.ActiveProfile is { IsServer: true }
        ? Core.Game.ServerExecutable.FileName : Core.Game.GameLauncher.ClientExeName);

    [RelayCommand]
    private void PickGameDir() => _main.ActiveProfile?.BrowseGameDirCommand.Execute(null);

    /// <summary>Строка под кнопками: какие папки и какая игра.</summary>
    public string ProfileInfo => Profile is { } r
        ? Loc.T("mods.profileInfo", r.GameVersion?.ToString() ?? Loc.T("common.notFound"), string.Join("; ", r.ModDirs))
        : IsHosted ? Loc.T("hosted.reading")
        : IsRemote ? Loc.T("mods.remoteWaiting")
        : Loc.T("mods.noProfile");

    /// <summary>Агент удалённого сервера ответил первый раз: стали известны его папки и версия игры.</summary>
    internal void NotifyProfileRead()
    {
        OnPropertyChanged(nameof(ProfileInfo));
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(HostedVersionMissing));
    }

    /// <summary>Сменился профиль или его настройки — перечитываем моды с диска, ответ модбазы про другой набор сбрасываем.</summary>
    public void OnProfileSwitched()
    {
        if (CheckCommand.IsRunning) CheckCancelCommand.Execute(null); // проверка прежнего профиля новому не нужна
        ModDbAnswers = new(StringComparer.OrdinalIgnoreCase);
        CheckedAt = null; // ответ модбазы сброшен — для сводки обновления снова «ещё не проверялись»
        _agent.Reset();
        _hosted.Reset();
        _worldJob.SetProfile(IsHosted ? _main.ActiveProfile?.Model.Id : null);
        WorldJobText = _worldJob.Text;
        ForgetSaidJob();
        Locals = [];
        OnPropertyChanged(nameof(IsLocalProfile));
        OnPropertyChanged(nameof(IsHosted));
        OnPropertyChanged(nameof(HostedVersionMissing));
        OnPropertyChanged(nameof(ProfileInfo));
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(GameMissing));
        OnPropertyChanged(nameof(GameMissingText));
        OnPropertyChanged(nameof(SummaryTitle));
        Selected = null;
        Card = null;
        if (IsRemote)
        {
            // список придёт с первым ответом агента (OnRemoteStatus)
            Rebuild();
            StatusText = Loc.T("mods.remoteWaiting");
            return;
        }
        if (IsHosted)
        {
            // по FTP читаем, только когда вкладка открыта: смена профиля или правка настроек не должна ходить на сервер зря
            Rebuild();
            StatusText = Loc.T("hosted.reading");
            if (_main.SelectedTab == AppTab.Mods) _ = _hosted.ReloadAsync();
            return;
        }
        ReloadLocal();
        StatusText = Loc.T("mods.readFromDisk");
        AutoCheck();
    }

    /// <summary>
    /// Автопроверка обновлений открытого профиля, если она включена и версия игры известна: при смене профиля, у удалённого
    /// сервера и у хостера — после первого чтения модов. Проверку прежнего профиля смена отменяет, но её задача кончается
    /// позже, продолжением в потоке окна, и до тех пор команда занята. Тогда новая проверка начинается сразу после неё,
    /// а не пропадает молча, как раньше.
    /// </summary>
    internal void AutoCheck()
    {
        if (!_main.AutoCheckUpdates || Profile?.GameVersion is null || _main.ActiveProfile is not { } profile) return;
        if (CheckCommand.CanExecute(null))
        {
            CheckCommand.Execute(null);
            return;
        }
        // идёт неотменённая проверка — она про этот же профиль: смена профиля отменяет прежнюю. Так бывает у хостера,
        // когда первое чтение модов запустила сама проверка
        if (!CheckCommand.IsCancellationRequested || CheckCommand.ExecutionTask is not { IsCompleted: false } running) return;
        _ = CheckAfterAsync(running, profile);
    }

    private async Task CheckAfterAsync(Task previous, ProfileViewModel profile)
    {
        await Task.WhenAny(previous); // итог отменённой проверки (и её ошибку) показывает она сама
        // профиль за это время сменили ещё раз — проверку нового запустит уже та смена
        if (_main.ActiveProfile == profile) AutoCheck();
    }

    /// <summary>Перечитать папки модов (без сети) и перестроить таблицу с уже известными статусами.</summary>
    public void ReloadLocal() => _ = ReloadAsync();

    /// <summary>Перечитать моды и дождаться: у своей машины — с диска, у удалённого сервера — у агента.</summary>
    internal Task ReloadAsync() => Source.ReloadAsync();

    /// <summary>Новый статус агента удалённого сервера: моды там поменялись (здесь или в другом окне) — перечитываем.</summary>
    public void OnRemoteStatus(AgentStatus status) => _agent.OnStatus(status);

    // ---------- настройки модов ----------

    /// <summary>Сколько файлов настроек (ModConfig) у каждого мода профиля: modid → число.</summary>
    private Dictionary<string, int> _configCounts = new(StringComparer.OrdinalIgnoreCase);

    private void CountConfigs() => _configCounts = Source.ConfigCounts(Locals);

    public int ConfigCount(string modId) => _configCounts.GetValueOrDefault(modId);

    /// <summary>
    /// Перед записью настроек модов (вкладка «Настройки модов») — та же проверка, что перед изменением модов, и та же
    /// память ответа: у хостера — «останови сервер в панели хостера» (текст вопроса — <paramref name="hostedQuestion"/>).
    /// </summary>
    internal bool ConfirmIfRunning(ModTarget target, string? hostedQuestion = null) => _installer.ConfirmIfRunning(target, hostedQuestion);

    /// <summary>Числа конфигов удалённого сервера пришли позже списка модов — ссылка в карточке обновляется.</summary>
    internal void ShowConfigCounts(Dictionary<string, int> counts)
    {
        _configCounts = counts;
        Card?.NotifyConfigs();
    }

    /// <summary>Карточка → вкладка «Настройки модов», сразу на файлах этого мода.</summary>
    [RelayCommand]
    private void OpenModConfig(ModRowViewModel? row)
    {
        if (row is not null) _main.OpenModConfig(row.ModId, row.Name);
    }

    /// <summary>Перестроить таблицу по прочитанным модам и известному ответу модбазы.</summary>
    internal void Rebuild()
    {
        // без версии игры с модбазой не сравнить — покажем только локальное
        var game = Profile?.GameVersion ?? ModVersion.ParseOrNull("0.0")!;
        var remote = Profile?.GameVersion is null ? new Dictionary<string, ModDbResult>() : ModDbAnswers;
        var policy = _main.ActiveProfile?.Model.ToPolicy() ?? ModPolicy.Empty;
        var results = UpdateChecker.Evaluate(Locals, remote, game, _main.AllowUnstable, policy);
        UpdateCount = results.Count(r => r.Status == ModStatus.UpdateAvailable && r.LatestCompatible?.MainFile is not null);
        ModCount = results.Count;
        DependencyIssues = Dependencies.FindIssues(Locals);
        CountConfigs();

        // после операции таблица строится заново — выбор остаётся на том же моде
        var keep = Card?.Row;
        var pins = _main.ActiveProfile?.Model.PinnedMods ?? new Dictionary<string, string>();
        var kind = _main.ActiveProfile?.Model.Kind ?? ProfileKind.Client;
        // строки, которые не изменились, остаются прежними объектами (ModRowViewModel.SameAs): переключение одного мода
        // меняет в таблице одну строку. Вид таблицы обновляется один раз, в конце, и новые строки встают на места
        // прежних: опустей он между Clear и Add, таблица прокрутилась бы к началу — список прыгал бы вверх
        var previous = new Dictionary<string, ModRowViewModel>(StringComparer.Ordinal);
        foreach (var row in Rows)
            if (!_renew.Contains(row.FilePath)) previous.TryAdd(row.FilePath, row);
        _renew.Clear();
        _rebuilding = true;
        try
        {
            using (View.DeferRefresh())
            {
                Rows.Clear();
                foreach (var r in results)
                {
                    var modId = r.Local.Info?.ModId;
                    var fresh = new ModRowViewModel(r, DependencyIssues, modId is not null && pins.ContainsKey(modId), kind, Profile?.GameVersion,
                        modId is null ? null : _cardLogos.GetValueOrDefault(modId));
                    Rows.Add(previous.TryGetValue(fresh.FilePath, out var was) && was.SameAs(fresh) ? was : fresh);
                }
            }
        }
        finally
        {
            _rebuilding = false;
        }

        UpdatesAvailable = Rows.Count(r => r.Kind == ModStatus.UpdateAvailable);
        ProblemCount = Rows.Count(r => r.IsProblem);
        PinnedCount = Rows.Count(r => r.IsPinned);
        DisabledCount = Rows.Count(r => !r.IsEnabled);
        UnneededCount = Rows.Count(r => r.IsUnneeded);
        if (UnneededCount == 0 && Filter == ModFilter.Unneeded) Filter = ModFilter.All; // таких модов не осталось — показываем всё
        OnPropertyChanged(nameof(ShownText));

        // выбор — на том же моде или снят. Таблица, у которой выбранную строку заменили или убрали, сама выбирает соседнюю,
        // а её карточку выше не строили: соседняя осталась бы выделенной при сводке справа, и щелчок по ней ничего бы не дал
        var same = keep is null ? null
            : Rows.FirstOrDefault(r => r.FilePath == keep.FilePath) ?? Rows.FirstOrDefault(r => r.ModId.Length > 0 && r.ModId == keep.ModId);
        if (same is null)
        {
            Selected = null;
            Card = null; // мод удалили
        }
        else
        {
            // строка та же — карточку всё равно строим заново (числа конфигов, версия игры могли поменяться; что она
            // уже узнала, новая берёт у прежней). Строка новая — карточку построит выбор
            var refresh = same == Selected || ReferenceEquals(same, Card?.Row);
            if (same != Selected) Selected = same;
            if (refresh) ShowCard(same);
        }
        if (IsHistory) _ = LoadHistoryAsync(); // моды поменялись — в истории появилось новое
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task CheckAsync(CancellationToken ct)
    {
        if (Profile is null || _main.ActiveProfile is not { } owner) return;
        // у хостера проверка начинается с чтения модов по FTP — во время копии мира оно ждало бы её конца
        if (ModTarget.For(owner.Model) is { IsHosted: true } hosted && RejectWorldJob(hosted, tell: false)) return;

        try
        {
            // скан сервера у хостера — сеть и первое чтение долгое: моложе 10 с не перечитываем (автопроверка идёт сразу за чтением)
            if (!(IsHosted && _hosted.IsFresh)) await ReloadAsync();
            if (_main.ActiveProfile != owner) return; // пока читали папки, профиль сменили — проверка уже не про него
            if (Profile?.GameVersion is not { } game)
            {
                StatusText = Loc.T("mods.noGameVersion");
                return;
            }
            var progress = new Progress<string>(s => { if (_main.ActiveProfile == owner) StatusText = s; });
            var remote = await _service.FetchRemoteAsync(Locals, progress, ct);
            // ответ модбазы — про моды того профиля, с которого начали; на другом он показал бы чужие обновления
            if (_main.ActiveProfile != owner) return;
            ModDbAnswers = remote;
            Rebuild();
            CheckedAt = DateTime.Now;
            StatusText = Loc.T("mods.checkedAt", Dates.ClockSeconds(CheckedAt.Value), game);
        }
        // отменили сменой профиля (или упала проверка прежнего) — строка состояния уже про новый профиль, не перебиваем её
        catch (OperationCanceledException) when (_main.ActiveProfile == owner)
        {
            StatusText = Loc.T("mods.checkCancelled");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            if (_main.ActiveProfile == owner) StatusText = Loc.T("common.errorWith", ex.Message);
        }
    }

    // ---------- включение / выключение ----------

    /// <summary>Переключения модов идут по одному, в порядке щелчков (см. <see cref="ToggleEnabledAsync"/>).</summary>
    private readonly SemaphoreSlim _toggleGate = new(1, 1);

    /// <summary>
    /// Включить или выключить мод. Команда не занята, пока идёт переключение: занятая команда выключает всех, кто к ней
    /// привязан, — переключатели всех строк и карточки гасли на время записи и перечитывания папки модов (у сотни
    /// архивов — секунду-другую, с копиями миров — дольше). Поэтому щелчок во время переключения встаёт в очередь, а
    /// его строка берётся из таблицы, перечитанной к тому времени: два щелчка по одному переключателю возвращают мод как
    /// было, а не выключают его дважды. Щелчок относится к профилю, в котором его сделали: сменили профиль, пока щелчок
    /// ждал очереди, — он отменяется, иначе мод с тем же файлом переключился бы в другом профиле (клон с той же папкой
    /// модов, второй сервер у того же хостера).
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleEnabledAsync(ModRowViewModel? row)
    {
        var owner = _main.ActiveProfile;
        await _toggleGate.WaitAsync();
        try
        {
            if (_main.ActiveProfile != owner) return; // строки прежнего профиля уже заменены — сбрасывать нечего
            if (row is not null && !Rows.Contains(row))
                row = Rows.FirstOrDefault(r => r.FilePath == row.FilePath); // таблицу построили заново; мода нет — нечего переключать
            if (row is not null) _renew.Add(row.FilePath);
            await ToggleOneAsync(row);
        }
        finally
        {
            _toggleGate.Release();
        }
    }

    private async Task ToggleOneAsync(ModRowViewModel? row)
    {
        if (row?.Local.Info is null || CurrentTarget() is not { } target)
        {
            // мод без modinfo переключить нечем (игра узнаёт мод по modid), профиль не читается — вернуть галочку как было
            if (row is not null) Rebuild();
            return;
        }

        if (target.IsHosted)
        {
            // у хостера запущен ли сервер, видно только по логу: не остановлен — спрашиваем «останови в панели хостера»;
            // мод хостера и папку менять нельзя; идёт копия мира — соединение занято ею
            if (RejectHostReadOnly(row) || RejectWorldJob(target) || !await _installer.ConfirmIfRunningAsync(target))
            {
                Rebuild(); // вернуть галочку как было (без сети)
                return;
            }
        }
        else if (_installer.IsRunning(target))
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

        await _installer.CopyWorldsAsync(target, side: ModSides.Parse(row.Local.Info.Side));
        try
        {
            await ModTargets.SetEnabledAsync(target, row.Local, enabled: !row.IsEnabled);
            StatusText = WithStartHint(_installer.WithWorldCopies(Loc.T(row.IsEnabled ? "mods.disabledOk" : "mods.enabledOk", row.Name)),
                target.IsHosted);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
                                       or HttpRequestException or TaskCanceledException)
        {
            Error(Loc.T("mods.toggleFailed", ErrorText(target, ex)));
        }
        await ReloadAsync();
    }

    // ---------- удаление ----------

    [RelayCommand]
    private async Task DeleteAsync(ModRowViewModel? row)
    {
        if (row is null || Profile is null || ChangeTarget() is not { } target) return;
        if (RejectHostReadOnly(row)) return;

        // у хостера корзины на сервере нет: свой вопрос (файл удалится сразу) и строка про копию на этой машине
        var question = target.IsHosted
            ? Loc.T("hosted.deleteConfirm", row.Name, row.Installed) + "\n\n" + Loc.T("hosted.deleteNote")
            : Loc.T("mods.deleteConfirm", row.Name, row.Installed) + (_installer.IsRunning(target) ? "\n\n" + Loc.T("mods.deleteRunning") : "");
        if (!Confirm(question))
            return;
        if (target.IsHosted && !await _installer.ConfirmIfRunningAsync(target)) return;
        await _installer.CopyWorldsAsync(target, side: ModSides.Parse(row.Local.Info?.Side));

        using var history = ModHistory.Begin(ModHistorySource.Manual);
        try
        {
            // свой — в Корзину, удалённый сервер — в его корзину, у хостера — копия на этой машине и удаление по FTP
            await ModTargets.DeleteAsync(target, row.Local, Ui.Platform.MoveToTrash);
            var done = target.IsHosted ? Loc.T("hosted.deletedOk", row.Name) : Loc.T("mods.deletedOk", row.Name);
            StatusText = WithStartHint(_installer.WithWorldCopies(done), target.IsHosted);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException
                                       or HttpRequestException or InvalidOperationException)
        {
            Error(Loc.T("mods.deleteFailed", row.Name, ErrorText(target, ex)));
        }
        await ReloadAsync();
    }

    // ---------- добавление из архива ----------

    [RelayCommand]
    private void AddFromFile()
    {
        var files = Ui.Platform.PickFiles(Loc.T("mods.pickZipTitle"), [new FileFilter(Loc.T("mods.zipFilter"), "*.zip")], multiple: true);
        if (files is not null) _ = AddFilesAsync(files);
    }

    /// <summary>Установка набора zip (кнопка или перетаскивание).</summary>
    public Task AddFilesAsync(IEnumerable<string> files) => _installs.AddFilesAsync(files);

    /// <summary>
    /// Нужна ли копия миров перед изменением мода этой стороны. Клиентский мод (шейдеры, интерфейс) в одиночной игре
    /// работает только в клиентской части и мир не меняет — копию не делаем. Не указано (несколько модов сразу,
    /// пак, откат сеанса) — как у игры, «обе стороны»: копируем.
    /// </summary>
    public static bool NeedsWorldCopy(ModSide side) => ModInstallService.NeedsWorldCopy(side);

    /// <summary>
    /// Итог операции: таблица перечитана, итоговая строка — в статус, что требует внимания — отдельным окном.
    /// <paramref name="notes"/> — мягкие предупреждения к поставленному (версия в файле мода не та, что на ModDB): в
    /// итоговую строку, отдельного окна ради них нет.
    /// </summary>
    internal async Task ReportAsync(string title, List<string> installed, List<string> problems, IReadOnlyList<string>? notes = null)
    {
        _main.HostedWatch?.ModsChangeDone(); // поставленное на сервер у хостера — оповещением «Обновлены моды»
        await ReloadAsync();
        _ = FetchNewModsAsync();
        // зависимости — по итоговому состоянию, а не на момент установки каждого мода
        if (installed.Count > 0)
            problems.AddRange(DependencyIssues.Select(i => Loc.T("report.dependency", i.Describe())));
        // предупреждения — в самый конец: подсказка «запусти сервер у хостера» важнее и не должна обрезаться
        StatusText = ModInstallService.WithNotes(WithStartHint(_installer.WithWorldCopies(installed.Count switch
        {
            0 => Loc.T("report.nothingChanged", title),
            <= 3 => $"{title}: " + string.Join("; ", installed),
            _ => Loc.T("report.changedCount", title, installed.Count), // длинный список — только число
        }), IsHosted && installed.Count > 0), notes);
        if (problems.Count == 0) return;
        // окно всё равно открывается — предупреждения в нём тоже, под списком поставленного
        var text = (installed.Count > 0 ? $"{title}:\n• " + string.Join("\n• ", installed) + "\n\n" : "")
                   + (notes is { Count: > 0 } ? string.Join("\n", notes.Distinct()) + "\n\n" : "")
                   + Loc.T("report.attention") + ":\n• " + string.Join("\n• ", problems);
        Ui.Warn(text);
    }

    /// <summary>Если модбазу уже опрашивали — досверить только что появившиеся моды, чтобы не висело «Не проверялся».</summary>
    private async Task FetchNewModsAsync()
    {
        if (ModDbAnswers.Count == 0) return;
        var fresh = Locals.Where(l => l.Info is not null && !ModDbAnswers.ContainsKey(l.Info.ModId)).ToList();
        if (fresh.Count == 0) return;
        try
        {
            foreach (var (id, r) in await _service.FetchRemoteAsync(fresh))
                ModDbAnswers[id] = r;
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
        if (row is not { CanUpdate: true } || CurrentTarget() is not { } target || RejectHostReadOnly(row)) return;
        Enqueue([QueueItemFor(target, row, row.Result.LatestCompatible!)], ModHistorySource.Update);
    }

    [RelayCommand]
    private void UpdateAll()
    {
        if (IsBusy && !IsQueueRunning) return;
        var todo = Rows.Where(r => r.CanUpdate && HostReadOnlyReason(r) is null).ToList();
        if (todo.Count == 0) return;

        var list = string.Join("\n", todo.Select(r => $"• {r.Name}: {r.Installed} → {r.Latest}"));
        if (ChangeTarget() is not { } target || !Confirm(Loc.T("mods.updateAllConfirm", todo.Count, list))) return;
        Enqueue(todo.Select(r => QueueItemFor(target, r, r.Result.LatestCompatible!)), ModHistorySource.UpdateAll);
    }

    /// <summary>
    /// «Обновить всё» по готовому списку — для плашки раздела «Сервер» у хостера: тот же вопрос со списком и та же
    /// очередь (копии прежних версий, журнал, история «Обновить всё»), у хостера — та же проверка по логу перед вопросом
    /// «останови сервер». Возвращает пункты очереди, которые теперь ставят эти обновления: вставшие в очередь, а вместо
    /// мода, который там уже ждал или ставился (второй раз он не встаёт), — тот пункт. Отказались или вкладка занята
    /// другим — пусто.
    /// </summary>
    internal async Task<IReadOnlyList<UpdateQueueItem>> UpdateAllAsync(ModTarget target, IReadOnlyList<UpdateQueueItem> items)
    {
        if (items.Count == 0 || !_queue.CanEnqueue || RejectWorldJob(target)) return [];
        var list = string.Join("\n", items.Select(i => $"• {i.Name}: {i.From} → {i.To}"));
        if (!Confirm(Loc.T("mods.updateAllConfirm", items.Count, list))) return [];
        if (target.IsHosted) await EnqueueCheckedAsync([.. items], [target], ModHistorySource.UpdateAll, null);
        else _queue.Enqueue(items, ModHistorySource.UpdateAll);
        return [.. items.Select(i => Queue.Contains(i) ? i : Queue.FirstOrDefault(q => q.State is QueueState.Waiting or QueueState.Working
                                                                                      && q.Target.ProfileId == i.Target.ProfileId
                                                                                      && string.Equals(q.ModId, i.ModId, StringComparison.OrdinalIgnoreCase)))
            .OfType<UpdateQueueItem>().Distinct()];
    }

    private static UpdateQueueItem QueueItemFor(ModTarget target, ModRowViewModel row, ModDbRelease release) =>
        new(target, row.ModId, row.Name, row.Installed, release.ModVersion ?? "?", release, null);

    // ---------- очередь установки ----------

    /// <summary>Очередь: обновления, откаты, выбранные версии. Ставятся по одному, можно добавлять на ходу.</summary>
    public ObservableCollection<UpdateQueueItem> Queue => _queue.Items;

    public bool IsQueueRunning
    {
        get => _queue.IsRunning;
        set => _queue.IsRunning = value;
    }

    public bool HasQueue => _queue.HasItems;
    public string QueueTitle => _queue.Title;
    public double QueueProgress => _queue.Progress;
    public bool HasQueueRetry => _queue.HasRetry;

    /// <summary>Кнопки «Обновить» доступны и пока идёт очередь — пункт просто встанет в конец.</summary>
    public bool CanQueue => _queue.CanEnqueue;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanQueue));

    // PropertyChanging флага прогона — тоже от имени вкладки, как раньше, когда флаг был её собственным полем
    private void OnQueueChanging(object? sender, PropertyChangingEventArgs e)
    {
        if (e.PropertyName == nameof(ModInstallQueue.IsRunning)) OnPropertyChanging(nameof(IsQueueRunning));
    }

    // окно привязано к подписям очереди под их прежними именами на вкладке
    private void OnQueueChanged(object? sender, PropertyChangedEventArgs e)
    {
        var name = e.PropertyName switch
        {
            nameof(ModInstallQueue.CanEnqueue) => nameof(CanQueue),
            nameof(ModInstallQueue.HasItems) => nameof(HasQueue),
            nameof(ModInstallQueue.Title) => nameof(QueueTitle),
            nameof(ModInstallQueue.Progress) => nameof(QueueProgress),
            nameof(ModInstallQueue.HasRetry) => nameof(HasQueueRetry),
            nameof(ModInstallQueue.IsRunning) => nameof(IsQueueRunning),
            _ => null,
        };
        if (name is not null) OnPropertyChanged(name);
    }

    /// <summary>
    /// Поставить в очередь; уже ждущий или ставящийся мод той же цели второй раз не добавляется.
    /// У каждого пункта своя цель: пункты разных профилей ставятся каждый в свой.
    /// </summary>
    public void Enqueue(IEnumerable<UpdateQueueItem> items, string source, string? undoes = null, bool runningConfirmed = false)
    {
        var list = items.ToList();
        // сервер у хостера занят копией мира — установка ждала бы её конца: сразу сказать, ничего не ставить
        if (list.Select(i => i.Target).DistinctBy(t => t.ProfileId).Any(t => RejectWorldJob(t))) return;
        List<ModTarget> hosted = runningConfirmed ? [] : [.. list.Select(i => i.Target).Where(t => t.IsHosted).DistinctBy(t => t.ProfileId)];
        if (hosted.Count == 0)
        {
            _queue.Enqueue(list, source, undoes, runningConfirmed);
            return;
        }
        // вкладка занята другим — сразу, как и без проверки (очередь не добавила бы и сама)
        if (!_queue.CanEnqueue) return;
        _ = EnqueueCheckedAsync(list, hosted, source, undoes);
    }

    /// <summary>
    /// У хостера вопрос очереди — после быстрой проверки по логу: сервер остановлен — очередь не спросит. Вопрос
    /// выключен или «Да» помнится — проверки нет, и пункты встают в очередь сразу, как раньше. Пока идёт проверка, в
    /// строке статуса видно, чего ждём, а вкладка занята: перетащенный тем временем zip иначе начал бы свою установку,
    /// и очередь после проверки молча не взяла бы пункты.
    /// </summary>
    private async Task EnqueueCheckedAsync(List<UpdateQueueItem> items, List<ModTarget> hosted, string source, string? undoes)
    {
        // прогон очереди уже держит флаг занятости — тогда его не трогаем.
        // shortcut: прогон кончился посреди проверки и тут же началась другая операция — пункты не встанут, как и раньше
        // при занятой вкладке. Переделать, если такое заметят
        var claimed = !IsBusy;
        if (claimed) IsBusy = true;
        var before = StatusText;
        var checking = Loc.T("hosted.stopChecking");
        StatusText = checking;
        try
        {
            foreach (var target in hosted) await _installer.CheckHostedAsync(target);
        }
        finally
        {
            if (StatusText == checking) StatusText = before;
            if (claimed) IsBusy = false;
        }
        _queue.Enqueue(items, source, undoes);
    }

    /// <summary>
    /// Можно ли менять моды цели: свой профиль и удалённый — вопрос, если игра или сервер запущены; у хостера — проверка
    /// по логу и вопрос «останови сервер в панели хостера» с общей памятью ответа «Да» (окно вылета спрашивает так же).
    /// </summary>
    internal Task<bool> ConfirmIfRunningAsync(ModTarget target, string? hostedQuestion = null) =>
        _installer.ConfirmIfRunningAsync(target, hostedQuestion);

    [RelayCommand]
    private void StopQueue() => _queue.Stop();

    [RelayCommand]
    private void RetryQueue() => _queue.Retry();

    [RelayCommand]
    private void CloseQueue() => _queue.Close();

    /// <summary>Очередь ставит через установку вкладки, занимает её флаг и после пунктов перечитывает её таблицу.</summary>
    private sealed class QueueHost(ModsViewModel owner) : IModQueueHost
    {
        public bool IsBusy
        {
            get => owner.IsBusy;
            set => owner.IsBusy = value;
        }

        // итог прогона: на сервере у хостера что-то поставилось — напомнить запустить его в панели и оповестить
        public void SetStatus(string text, IReadOnlyList<string> notes)
        {
            owner._main.HostedWatch?.ModsChangeDone();
            owner.StatusText = ModInstallService.WithNotes(
                WithStartHint(text, owner._queue.Items.Any(i => i.State == QueueState.Done && i.Target.IsHosted)), notes);
        }

        public bool IsActive(ModTarget target) => owner._installer.IsActive(target);

        // у хостера проверку по логу уже сделал Enqueue вкладки: остановлен — без вопроса
        public bool ConfirmIfRunning(ModTarget target) => owner._installer.ConfirmChecked(target);

        public Task<bool> InstallZipAsync(ModTarget target, string zip, List<string> installed, List<string> problems) =>
            owner._installer.InstallZipAsync(target, zip, interactive: false, installed, problems);

        public Task<bool> DownloadAndInstallAsync(ModTarget target, string modId, string name, ModDbRelease release,
            List<string> installed, List<string> problems, List<string> notes, Action<double> onProgress, CancellationToken ct) =>
            owner._installer.DownloadAndInstallAsync(target, modId, name, release, installed, problems, onProgress, ct, notes);

        public Task ReloadAsync() => owner.ReloadAsync();

        public Task FetchNewModsAsync() => owner.FetchNewModsAsync();
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
        // прежние копии удалённого сервера лежат на той машине — отсюда предлагаем только релизы модбазы.
        // «Установлена» — у релиза, чей это файл, даже если автор не поднял версию внутри (ReleaseMarks)
        return VersionOptions.Build(IsRemote ? null : ModBackupStore.ForProfile(profile.Profile), info.ModId,
            row.Local.Release?.ReleaseVersion ?? info.Version, releases);
    }

    /// <summary>Поставить выбранную версию (через очередь).</summary>
    public void InstallVersion(ModRowViewModel row, VersionOption option)
    {
        if (CurrentTarget() is { } target)
            Enqueue([new UpdateQueueItem(target, row.ModId, row.Name, row.Installed, option.Version, option.Release, option.Path)],
                ModHistorySource.Rollback);
    }

    [RelayCommand]
    private async Task RollbackAsync(ModRowViewModel? row)
    {
        if (row?.Local.Info is null || Profile is null || (IsBusy && !IsQueueRunning)) return;
        // выбрать версию можно и сейчас, но поставить её не выйдет, пока копия мира держит соединение, — сказать сразу
        if (_main.ActiveProfile is { } active && ModTarget.For(active.Model) is { IsHosted: true } hosted && RejectWorldJob(hosted)) return;

        StatusText = Loc.T("mods.findingVersions", row.Name);
        var options = await LoadVersionOptionsAsync(row);
        StatusText = "";

        if (AppWindows.Current.PickVersion(Loc.T("rollback.heading", row.Name, row.Installed), options) is { } choice)
            InstallVersion(row, choice);
    }

    // ---------- зависимости ----------

    [RelayCommand]
    private Task FixDependenciesAsync() => _installs.FixDependenciesAsync();

    // ---------- установка из каталога ----------

    /// <summary>
    /// modid установленных модов активного профиля (для отметки «установлен» в каталоге). Каталог говорит версиями ModDB:
    /// у файла релиза, в котором автор не поднял версию (<see cref="ReleaseMarks"/>), — версия релиза, иначе карточка звала
    /// бы обновиться до того же релиза.
    /// </summary>
    public IReadOnlyDictionary<string, string> InstalledVersions =>
        Locals.Where(l => l.Info is not null)
            .GroupBy(l => l.Info!.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Release?.ReleaseVersion ?? g.First().Info!.Version ?? "", StringComparer.OrdinalIgnoreCase);

    public event EventHandler? LocalModsChanged;

    /// <summary>Моды профиля перечитаны: каталог обновляет отметки «установлен» и версию игры.</summary>
    internal void NotifyLocalModsChanged() => LocalModsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Скачать и поставить релиз из каталога — в текущий профиль и, если выбрали, ещё в другие (каждому свой релиз),
    /// затем предложить доставить зависимости: в каждом профиле, куда мод встал, — по одному общему вопросу.
    /// </summary>
    public Task<bool> InstallFromCatalogAsync(string modId, string name, ModDbRelease release,
        IReadOnlyList<(ModTarget Target, ModDbRelease Release)>? also = null) =>
        _installs.InstallFromCatalogAsync(modId, name, release, also);

    // ---------- модпаки и код набора ----------

    [RelayCommand]
    private Task ExportPackAsync() => _packs.ExportAsync();

    [RelayCommand]
    private async Task ImportPackAsync()
    {
        var files = Ui.Platform.PickFiles(Loc.T("packi.openTitle"), [new FileFilter(Loc.T("pack.filter"), "*" + PackManifest.Extension)], multiple: false);
        if (files is { Count: > 0 }) await ImportPackFileAsync(files[0]);
    }

    public Task ImportPackFileAsync(string path) => _packs.ImportFileAsync(path);

    /// <summary>Код набора включённых модов — в буфер обмена (см. <see cref="ModCode"/>).</summary>
    [RelayCommand]
    private Task CopyModCodeAsync() => _codes.CopyAsync();

    /// <summary>Поставить набор по коду — обычным импортом модпака без файлов внутри.</summary>
    [RelayCommand]
    private Task PasteModCodeAsync() => _codes.PasteAsync();

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
        if (row?.PageUrl is { } url) Ui.OpenUrl(url);
    }

    [RelayCommand]
    private void ShowFile(ModRowViewModel? row)
    {
        if (IsLocalProfile && row is not null && Path.Exists(row.FilePath)) Ui.ShowInFolder(row.FilePath);
    }

    [RelayCommand]
    private void OpenModsFolder()
    {
        if (IsLocalProfile && Profile?.InstallDir is { } dir && Directory.Exists(dir)) Ui.OpenFolder(dir);
    }

    // ---------- сервер у хостера ----------

    /// <summary>К итогу операции на сервере у хостера: изменения вступят в силу, когда сервер запустят в панели хостера.</summary>
    internal static string WithStartHint(string text, bool hostedChanged) =>
        hostedChanged ? text + " · " + Loc.T("hosted.startHint") : text;

    /// <summary>Почему этот мод на сервере у хостера нельзя менять (мод хостера, папка); null — можно или это не хостер.</summary>
    private string? HostReadOnlyReason(ModRowViewModel row) => IsHosted ? Core.Hosting.HostedMods.ReadOnlyReason(row.Local) : null;

    /// <summary>Мод на сервере у хостера только для чтения — сказать об этом в статусе; true — действие не делаем.</summary>
    private bool RejectHostReadOnly(ModRowViewModel row)
    {
        if (HostReadOnlyReason(row) is not { } reason) return false;
        StatusText = reason;
        return true;
    }

    // ---------- сервер у хостера: копия мира держит соединение

    /// <summary>
    /// У открытого сервера у хостера идёт операция с копией мира (вкладка «Копии» или расписание): «Идёт копия мира
    /// (Скачиваю мир: 12 МБ из 90 МБ). Подожди или отмени её во вкладке «Копии».» Она держит соединение профиля до конца
    /// передачи, поэтому чтение модов и их изменения ждут её конца, а команды, которые пошли бы на сервер, говорят то же
    /// самое в строке статуса. Пусто — не идёт.
    /// </summary>
    [ObservableProperty] private string _worldJobText = "";

    /// <summary>Что сказала строка статуса, когда команду не начали из-за копии мира (кончится копия — строку убрать).</summary>
    private string? _worldJobSaid;

    /// <summary>Чью копию назвала строка статуса: её ход строка повторяет, её конец строку убирает (профиль может быть и не открытый).</summary>
    private HostedWorldJob? _saidJob;

    /// <summary>Идущая операция с копией мира у сервера у хостера этой цели; не у хостера или не идёт — null.</summary>
    private HostedWorldJob? WorldJob(ModTarget target) => target.IsHosted ? _main.HostedWorldJobs?.Running(target.ProfileId) : null;

    /// <summary>У сервера у хостера этой цели идёт операция с копией мира: в его сеть сейчас не ходить.</summary>
    internal bool IsWorldJobRunning(ModTarget target) => WorldJob(target) is not null;

    /// <summary>
    /// У сервера у хостера цели идёт операция с копией мира — сказать это в строке статуса и вернуть true: операцию не
    /// начинать, иначе она молча ждала бы конца передачи. Не у хостера или копии нет — false. Команду нажали
    /// (<paramref name="tell"/>) — ещё и окно с причиной: строка статуса внизу повторяет плашку сверху, и отказ на клик
    /// выглядел так, будто кнопка не сработала. Фоновое чтение и автопроверка — только строка статуса.
    /// </summary>
    internal bool RejectWorldJob(ModTarget target, bool tell = true)
    {
        if (WorldJob(target) is not { } job) return false;
        if (!ReferenceEquals(job, _saidJob))
        {
            ForgetSaidJob();
            _saidJob = job;
            job.PropertyChanged += OnSaidJobChanged;
            _ = ClearWhenDoneAsync(job);
        }
        StatusText = _worldJobSaid = SaidText(job);
        if (tell) Ui.Warn(StatusText);
        return true;
    }

    // копия чужого профиля (пункт «Установить также в») — с именем сервера: вкладка «Копии» открытого профиля про неё не знает
    private string SaidText(HostedWorldJob job) =>
        IsHosted && job.ProfileId == _main.ActiveProfile?.Model.Id ? HostedWorldBusy.Describe(job) : HostedWorldBusy.Describe(job, job.Target.Name);

    private void OnSaidJobChanged(object? sender, PropertyChangedEventArgs e) => RefreshSaidJob();

    /// <summary>Строка статуса ещё говорит о копии — пересказать её ход на текущем языке.</summary>
    private void RefreshSaidJob()
    {
        if (_saidJob is not { } job || _worldJobSaid is not { } said || StatusText != said) return;
        StatusText = _worldJobSaid = SaidText(job);
    }

    private async Task ClearWhenDoneAsync(HostedWorldJob job)
    {
        await job.Completion; // исключений не бросает
        if (ReferenceEquals(job, _saidJob)) ClearSaidJob();
    }

    /// <summary>Копия кончилась: строка статуса «идёт копия мира» уже неправда — убрать её.</summary>
    private void ClearSaidJob()
    {
        if (_worldJobSaid is { } said && StatusText == said) StatusText = "";
        ForgetSaidJob();
    }

    private void ForgetSaidJob()
    {
        if (_saidJob is { } old) old.PropertyChanged -= OnSaidJobChanged;
        _saidJob = null;
        _worldJobSaid = null;
    }

    /// <summary>Цель для изменения модов: как <see cref="CurrentTarget"/>, но пока у сервера у хостера идёт копия мира — null и причина в статусе.</summary>
    internal ModTarget? ChangeTarget() => CurrentTarget() is { } target && !RejectWorldJob(target) ? target : null;

    /// <summary>
    /// Копия мира началась или кончилась. Кончилась — соединение свободно: строка статуса «идёт копия мира» уже неправда, а
    /// отложенное чтение модов или сверка с сервером — сейчас, если вкладка открыта.
    /// </summary>
    private void OnWorldJobBusyChanged()
    {
        if (_worldJob.IsBusy) return;
        // копия открытого профиля кончилась — строку убрать сразу, не дожидаясь продолжения ClearWhenDoneAsync
        if (_saidJob is null or { IsFinished: true }) ClearSaidJob();
        _hosted.Resume();
    }

    /// <summary>Текст ошибки операции с целью: у хостера — словами (вход, права, место), у остальных — текст исключения.</summary>
    private static string ErrorText(ModTarget target, Exception ex) => target.IsHosted ? ModTargets.Describe(target, ex) : ex.Message;

    private static bool Confirm(string text) =>
        Ui.Confirm(text);

    private static void Error(string text) =>
        Ui.Warn(text);
}

using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Что окну нового клиентского профиля нужно от системы: план и само копирование, запущена ли игра, свободное место,
/// размер миров и стандартная папка данных. По умолчанию всё настоящее; тесты подменяют.
/// </summary>
public sealed record ClientProfileServices
{
    /// <summary>Что будет скопировано; бросает InvalidOperationException с понятным текстом, если создать профиль нельзя.</summary>
    public Func<GameProfile, ClientCloneOptions, ClientClonePlan> Plan { get; init; } = ClientProfileCloner.Plan;

    /// <summary>Скопировать по плану и вернуть новый профиль.</summary>
    public Func<ClientClonePlan, IProgress<CloneProgress>, CancellationToken, Task<GameProfile>> Apply { get; init; } = ClientProfileCloner.ApplyAsync;

    /// <summary>Игра этого профиля запущена.</summary>
    public Func<GameProfile, bool> IsGameRunning { get; init; } = GameProcess.IsRunning;

    /// <summary>Свободное место на диске папки; не узнать — null.</summary>
    public Func<string, long?> FreeSpace { get; init; } = ProfileCopyText.FreeSpace;

    /// <summary>Сколько весят миры и карты в папке данных.</summary>
    public Func<string?, long> WorldBytes { get; init; } = ClientProfileViewModel.WorldBytes;

    /// <summary>Стандартная папка данных игры (у основы своей папки нет); null — %APPDATA%\VintagestoryData.</summary>
    public string? DefaultDataDir { get; init; }
}

/// <summary>
/// Окно «Новый клиентский профиль» на основе существующего или его клон: своя папка данных, моды общие / своя копия /
/// с нуля, настройки игры — копия или чистые, миры — по желанию. Пока окно открыто, план копирования пересчитывается в
/// фоне (после паузы в правке); устаревший расчёт отбрасывается. Копирование — с ходом и отменой. Готово —
/// <see cref="ProfileCopyViewModel{TOptions,TPlan}.Finished"/> с новым профилем (папка уже создана), окно закрывается
/// с ним. Название, папка, пересчёт и копирование — общие с клоном сервера (<see cref="ProfileCopyViewModel{TOptions,TPlan}"/>);
/// здесь — моды, настройки, миры и запущенная игра.
/// </summary>
public sealed partial class ClientProfileViewModel : ProfileCopyViewModel<ClientCloneOptions, ClientClonePlan>
{
    /// <summary>План — это обход папки данных: считаем не на каждую букву, а после короткой паузы.</summary>
    public static readonly TimeSpan RecalculateDelay = TimeSpan.FromMilliseconds(300);

    private readonly GameProfile _source;
    private readonly bool _clone;
    private readonly ClientProfileServices _services;
    private readonly string _home;     // от неё предлагается папка нового профиля (…\ClientProfiles\имя)
    private bool _isGameRunning;
    private double _progressValue;
    private bool _hasWorlds;
    private string _worldsText = "";

    /// <param name="source">Профиль-основа.</param>
    /// <param name="clone">true — «Клонировать…» (миры по умолчанию копируются), false — «+ Клиент».</param>
    /// <param name="suggestedName">Название нового профиля по умолчанию.</param>
    /// <param name="services">Система (тесты подменяют); null — настоящая.</param>
    public ClientProfileViewModel(GameProfile source, bool clone, string suggestedName, ClientProfileServices? services = null)
        : base(Ui.Timer(RecalculateDelay))
    {
        _source = source;
        _clone = clone;
        _services = services ?? new ClientProfileServices();
        _home = string.IsNullOrWhiteSpace(source.DataDir) ? _services.DefaultDataDir ?? GameInstall.DefaultDataDir : source.DataDir;

        Title = Loc.T(clone ? "clone.title" : "newprofile.titleClient");
        Heading = clone ? Loc.T("clone.heading", source.Name) : Loc.T("newprofile.titleClient");
        Intro = clone ? Loc.T("cprofile.introClone") : Loc.T("cprofile.introNew", source.Name);
        GoText = Loc.T(clone ? "clone.go" : "newprofile.create");

        var modDirs = ProfileResolver.Resolve(source).ModDirs;
        SharedModsHint = Loc.T("cprofile.modsSharedHint", source.Name, modDirs.Count == 0 ? "—" : string.Join("; ", modDirs));
        // у основы модов нет вовсе (игра ещё не запускалась) — делить и копировать нечего: только с нуля
        HasMods = modDirs.Count > 0;
        _mods = HasMods ? ClientModsMode.Shared : ClientModsMode.Empty;

        SetInitial(suggestedName, SuggestTarget(suggestedName));
        RecalculateNow();
    }

    /// <summary>Заголовок окна: «Клонировать профиль» или «Новый клиентский профиль».</summary>
    public string Title { get; }

    /// <summary>Крупная надпись в окне: «Клонировать «…»» или «Новый клиентский профиль».</summary>
    public string Heading { get; }

    public string Intro { get; }

    /// <summary>Надпись на главной кнопке: «Клонировать» или «Создать».</summary>
    public string GoText { get; }

    /// <summary>Откуда основа берёт моды (для варианта «Общие моды»).</summary>
    public string SharedModsHint { get; }

    /// <summary>У основы есть папки модов: «Общие моды» и «Своя копия» доступны. Нет — только с нуля.</summary>
    public bool HasMods { get; }

    [ObservableProperty] private ClientModsMode _mods;

    /// <summary>Настройки игры — как у основы (true) или чистые (false).</summary>
    [ObservableProperty] private bool _copySettings = true;

    /// <summary>Копировать миры одиночной игры и карты (галочка видна, только если в основе миры есть).</summary>
    [ObservableProperty] private bool _copyWorlds;

    partial void OnModsChanged(ClientModsMode value) => Recalculate();
    partial void OnCopySettingsChanged(bool value) => Recalculate();
    partial void OnCopyWorldsChanged(bool value) => Recalculate();

    /// <summary>В основе есть миры (не пустые): показать галочку «Копировать миры» с их размером.</summary>
    public bool HasWorlds
    {
        get => _hasWorlds;
        private set => SetProperty(ref _hasWorlds, value);
    }

    /// <summary>«Скопировать миры одиночной игры и карты (1,2 ГБ)».</summary>
    public string WorldsText
    {
        get => _worldsText;
        private set => SetProperty(ref _worldsText, value);
    }

    /// <summary>Игра основы запущена, а миры копируются: копия открытого мира может получиться битой — нельзя.</summary>
    public bool IsGameRunning
    {
        get => _isGameRunning;
        private set => SetProperty(ref _isGameRunning, value);
    }

    /// <summary>Ход копирования: доля 0..1.</summary>
    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    /// <summary>
    /// Последний начатый расчёт плана (окну не нужен: по нему тесты ждут ответа). Неожиданная ошибка расчёта уходит и
    /// в обработчик сбоев программы.
    /// </summary>
    public Task Calculation => LastCalculation;

    /// <summary>Окно открылось: размер миров основы — в фоне (миры бывают большими).</summary>
    public async Task LoadAsync()
    {
        var bytes = await Task.Run(() => _services.WorldBytes(_source.DataDir));
        if (bytes <= 0) return;
        WorldsText = Loc.T("cprofile.worlds", Sizes.Format(bytes));
        HasWorlds = true;
        CopyWorlds = _clone;
    }

    /// <summary>Сколько весят миры и карты в папке данных; недоступное пропускается.</summary>
    public static long WorldBytes(string? dataDir) =>
        string.IsNullOrWhiteSpace(dataDir) ? 0 : ProfileCopyText.FolderBytes(ClientProfileCloner.WorldFolders(dataDir));

    // ---- для общей части

    protected override string SuggestTarget(string? name) =>
        (name ?? "").Trim().Length == 0 ? "" : ClientProfileLayout.SuggestDir(_home, name!);

    protected override ClientCloneOptions ReadOptions() => new()
    {
        Name = Clean(Name),
        TargetDir = Clean(TargetDir),
        Mods = Mods,
        CopySettings = CopySettings,
        CopyWorlds = CopyWorlds,
    };

    // открытый в игре мир копировать нельзя; без миров запущенная игра не мешает (и про неё не спрашиваем)
    protected override (ClientClonePlan Plan, bool Blocked) MakePlan(ClientCloneOptions options) =>
        (_services.Plan(_source, options), options.CopyWorlds && _services.IsGameRunning(_source));

    protected override void ShowBlocked(bool blocked) => IsGameRunning = blocked;

    protected override PlanFacts Facts(ClientClonePlan plan) => new(plan.Files.Count, plan.TotalBytes, plan.ModSources, plan.ModNotes);

    protected override string SkippedText => Loc.T("cprofile.summarySkipped");

    // неожиданная ошибка расчёта — в обработчик сбоев программы, в окне её не показываем
    protected override bool ShowsUnexpectedPlanErrors => false;

    // сбой в обработчике Finished — как сбой копирования: форма возвращается с причиной
    protected override bool FinishedErrorsAreCopyErrors => true;

    // флаг «папку подставляет модель» поднимается до подбора папки, и сбой подстановки его не снимает
    protected override bool KeepsTargetFlagOnError => true;

    protected override long? FreeSpace(string dir) => _services.FreeSpace(dir);

    protected override Task<GameProfile> Apply(ClientClonePlan plan, IProgress<CloneProgress> progress, CancellationToken ct) =>
        _services.Apply(plan, progress, ct);

    protected override string? PickFolder(string title, string start) => Ui.Platform.PickFolder(title, start);

    protected override void LogError(Exception ex) => Ui.Platform.WriteCrashLog(ex);

    protected override void ShowProgress(double share) => ProgressValue = share;
}

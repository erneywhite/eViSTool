using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Чем окно клона серверного профиля трогает диск, таймер и окна системы. По умолчанию — настоящие
/// <see cref="ProfileCloner"/>, диск и таймер окна; тесты подменяют отдельные члены
/// (<c>CloneProfileServices.Default with { FreeSpace = … }</c>), чтобы проверить отказы, нехватку места, отмену и
/// устаревшие ответы, не трогая настоящего диска и не дожидаясь паузы.
/// </summary>
public sealed record CloneProfileServices
{
    public static CloneProfileServices Default { get; } = new();

    /// <summary>План копирования — обход папки данных (идёт в фоновом потоке). Клонировать нельзя — исключение с понятным текстом.</summary>
    public Func<GameProfile, CloneOptions, ClonePlan> Plan { get; init; } = ProfileCloner.Plan;

    /// <summary>Копирование по плану (идёт в фоновом потоке): ход — в progress, готовый профиль — ответ.</summary>
    public Func<ClonePlan, IProgress<CloneProgress>, CancellationToken, Task<GameProfile>> Apply { get; init; } =
        (plan, progress, ct) => ProfileCloner.ApplyAsync(plan, progress, ct);

    /// <summary>Свободное место на диске папки; не узнать — null.</summary>
    public Func<string, long?> FreeSpace { get; init; } = ProfileCopyText.FreeSpace;

    /// <summary>Сколько занимают резервные копии в папке данных (идёт в фоновом потоке).</summary>
    public Func<string?, long> BackupBytes { get; init; } = CloneProfileViewModel.BackupBytes;

    /// <summary>Таймер паузы перед пересчётом плана.</summary>
    public Func<TimeSpan, IUiTimer> Timer { get; init; } = Ui.Timer;

    /// <summary>Выбор папки («Обзор…»): заголовок, с какой папки начать; отменили — null.</summary>
    public Func<string, string?, string?> PickFolder { get; init; } = (title, start) => Ui.Platform.PickFolder(title, start);

    /// <summary>Неожиданная ошибка — в журнал программы.</summary>
    public Action<Exception> LogError { get; init; } = ex => Ui.Platform.WriteCrashLog(ex);
}

/// <summary>
/// Окно клона серверного профиля и импорта существующих данных сервера в новый профиль: название, новая папка данных,
/// какой конфиг (мир) взять, «тот же мир» или «новый мир», резервные копии, общие моды или своя копия. Пока окно
/// открыто, план пересчитывается в фоне после короткой паузы — сколько файлов и сколько места нужно; устаревший ответ
/// (условия успели поменяться) отбрасывается. «Клонировать» копирует с ходом и отменой; готово —
/// <see cref="ProfileCopyViewModel{TOptions,TPlan}.Finished"/> с новым профилем, окно закрывается с ним. Сбой или
/// отмена — форма возвращается, причина видна до следующей правки. Название, папка, пересчёт и копирование — общие с
/// окном клиентского профиля (<see cref="ProfileCopyViewModel{TOptions,TPlan}"/>); здесь — конфиг, мир, резервные копии
/// и моды.
/// </summary>
public sealed partial class CloneProfileViewModel : ProfileCopyViewModel<CloneOptions, ClonePlan>
{
    /// <summary>Пауза перед пересчётом плана: план — обход всей папки данных, считаем не на каждую букву.</summary>
    public static readonly TimeSpan PlanDelay = TimeSpan.FromMilliseconds(300);

    private readonly GameProfile _source;
    private readonly CloneProfileServices _services;
    private bool _hasBackups;
    private string _backupsText = "";
    private double _progress;

    /// <param name="source">Профиль-источник: его папка данных копируется (при импорте — папка существующих данных).</param>
    /// <param name="serverRunning">Сервер профиля запущен — копия мира может получиться битой, «Клонировать» недоступна.</param>
    /// <param name="importName">Не null — импорт существующих данных сервера в новый профиль с этим названием.</param>
    /// <param name="services">Диск, таймер и окна системы; по умолчанию — настоящие (тесты подменяют).</param>
    public CloneProfileViewModel(GameProfile source, bool serverRunning, string? importName = null, CloneProfileServices? services = null)
        : base((services ??= CloneProfileServices.Default).Timer(PlanDelay))
    {
        _source = source;
        _services = services;
        IsServerRunning = serverRunning;
        IsImport = importName is not null;
        Title = Loc.T(IsImport ? "newprofile.titleServer" : "clone.title");
        Heading = IsImport ? Loc.T("clone.headingImport", source.DataDir) : Loc.T("clone.heading", source.Name);
        SharedModsHint = Loc.T("clone.modsSharedHint", ProfileResolver.Resolve(source).InstallDir ?? "—");

        Configs = ProfileCloner.FindConfigs(source.DataDir ?? "")
            .Select(c => new Choice<ServerConfigInfo>(Loc.T("clone.configItem", c.FileName, c.WorldName ?? "?",
                c.SaveExists ? Sizes.Format(c.SaveBytes) : Loc.T("clone.configNoSave")), c))
            .ToList();
        _selectedConfig = Configs.FirstOrDefault();

        var name = importName ?? Loc.T("clone.copySuffix", source.Name);
        SetInitial(name, SuggestTarget(name) ?? "");
        _worldName = name;

        // резервные копии бывают большими — размер считаем в фоне
        BackupsCounting = CountBackupsAsync();
        RecalculateNow();
    }

    /// <summary>Заголовок окна: «Клонировать профиль» или, при импорте, «Новый серверный профиль».</summary>
    public string Title { get; }

    /// <summary>Крупная надпись: «Клон профиля «…»» или «Импорт данных сервера из …».</summary>
    public string Heading { get; }

    /// <summary>Импорт существующих данных сервера, а не клон профиля.</summary>
    public bool IsImport { get; }

    /// <summary>Сервер профиля запущен: предупреждение в окне, «Клонировать» недоступна.</summary>
    public bool IsServerRunning { get; }

    /// <summary>Пояснение к «Общей папке модов»: из какой папки оба профиля берут моды.</summary>
    public string SharedModsHint { get; }

    /// <summary>Конфиги сервера в папке-источнике (serverconfig.json первым) с миром и размером сохранения.</summary>
    public IReadOnlyList<Choice<ServerConfigInfo>> Configs { get; }

    /// <summary>Конфигов несколько — показать выбор мира.</summary>
    public bool HasConfigChoice => Configs.Count > 1;

    /// <summary>Какой конфиг станет serverconfig.json клона; null — конфигов в папке нет.</summary>
    [ObservableProperty] private Choice<ServerConfigInfo>? _selectedConfig;

    /// <summary>«Новый мир» (без сохранений); false — «Тот же мир».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBackups))]
    private bool _newWorld;

    /// <summary>Название нового мира. На состав файлов не влияет: план из-за него не пересчитывается.</summary>
    [ObservableProperty] private string _worldName;

    /// <summary>Скопировать и резервные копии (для «Того же мира»).</summary>
    [ObservableProperty] private bool _includeBackups;

    /// <summary>Общая папка модов с исходным профилем; false — своя копия модов.</summary>
    [ObservableProperty] private bool _shareMods = true;

    partial void OnSelectedConfigChanged(Choice<ServerConfigInfo>? value) => Recalculate();
    partial void OnNewWorldChanged(bool value) => Recalculate();
    partial void OnIncludeBackupsChanged(bool value) => Recalculate();
    partial void OnShareModsChanged(bool value) => Recalculate();

    /// <summary>В папке-источнике нашлись резервные копии — показать галочку «Скопировать и резервные копии».</summary>
    public bool HasBackups
    {
        get => _hasBackups;
        private set
        {
            if (SetProperty(ref _hasBackups, value)) OnPropertyChanged(nameof(ShowBackups));
        }
    }

    /// <summary>Надпись галочки: «Скопировать и резервные копии (1,2 ГБ)».</summary>
    public string BackupsText
    {
        get => _backupsText;
        private set => SetProperty(ref _backupsText, value);
    }

    /// <summary>Галочка резервных копий видна: они есть и выбран «Тот же мир» (новому миру чужие бэкапы не нужны).</summary>
    public bool ShowBackups => HasBackups && !NewWorld;

    public bool HasError => ErrorText.Length > 0;

    /// <summary>Форму можно править (не идёт копирование).</summary>
    public bool IsFormEnabled => !IsCopying;

    /// <summary>Ход копирования — доля 0..1.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    /// <summary>Идущий (или последний) пересчёт плана. Окно его не ждёт; тесты ждут, чтобы проверить итог.</summary>
    public Task Planning => LastCalculation;

    /// <summary>Подсчёт размера резервных копий (идёт в фоне с открытия окна).</summary>
    public Task BackupsCounting { get; }

    /// <summary>Сколько занимают Backups и BackupSaves в папке данных (недоступное не считается).</summary>
    public static long BackupBytes(string? dataDir) =>
        // без папки данных — ноль, а не «Backups» в текущей папке программы
        string.IsNullOrWhiteSpace(dataDir) ? 0
            : ProfileCopyText.FolderBytes(new[] { "Backups", "BackupSaves" }.Select(name => Path.Combine(dataDir, name)));

    private async Task CountBackupsAsync()
    {
        var dataDir = _source.DataDir;
        var bytes = await Task.Run(() => _services.BackupBytes(dataDir));
        if (bytes <= 0) return;
        BackupsText = Loc.T("clone.backups", Sizes.Format(bytes));
        HasBackups = true;
    }

    // ErrorText и IsCopying живут в общей части — зависящие от них свойства этого окна сообщают о себе здесь
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(ErrorText)) OnPropertyChanged(nameof(HasError));
        else if (e.PropertyName == nameof(IsCopying)) OnPropertyChanged(nameof(IsFormEnabled));
    }

    // ---- для общей части

    /// <summary>Папка по названию («…\ServerProfiles\название»); у источника нет папки данных — подставлять не от чего (null).</summary>
    protected override string? SuggestTarget(string name) =>
        string.IsNullOrWhiteSpace(_source.DataDir) ? null : ProfileCloner.SuggestTargetDir(_source.DataDir, name);

    protected override CloneOptions ReadOptions() => new()
    {
        Name = Clean(Name),
        TargetDir = Clean(TargetDir),
        NewWorld = NewWorld,
        WorldName = Clean(WorldName),
        IncludeBackups = IncludeBackups,
        ShareMods = ShareMods,
        // конфиг в папке один — выбора нет, но берём именно его: он может называться и не serverconfig.json
        ConfigFile = SelectedConfig?.Value.FileName ?? ProfileCloner.MainConfig,
    };

    // сервер запущен — план показываем, но клонировать нельзя
    protected override (ClonePlan Plan, bool Blocked) MakePlan(CloneOptions options) => (_services.Plan(_source, options), IsServerRunning);

    protected override PlanFacts Facts(ClonePlan plan) => new(plan.Files.Count, plan.TotalBytes, plan.ModSources, plan.ModNotes);

    protected override string SkippedText => Loc.T("clone.summarySkipped");

    // неожиданная ошибка расчёта — в журнал и причиной в окне, иначе «Клонировать» молча осталась бы выключенной
    protected override bool ShowsUnexpectedPlanErrors => true;

    protected override long? FreeSpace(string dir) => _services.FreeSpace(dir);

    // название мира на состав файлов не влияет — берём то, что в поле сейчас
    protected override ClonePlan BeforeCopy(ClonePlan plan) => plan with { Options = plan.Options with { WorldName = Clean(WorldName) } };

    protected override Task<GameProfile> Apply(ClonePlan plan, IProgress<CloneProgress> progress, CancellationToken ct) =>
        _services.Apply(plan, progress, ct);

    protected override string? PickFolder(string title, string start) => _services.PickFolder(title, start);

    protected override void LogError(Exception ex) => _services.LogError(ex);

    protected override void ShowProgress(double share) => Progress = share;
}

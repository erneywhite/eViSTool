using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
/// (условия успели поменяться) отбрасывается. «Клонировать» копирует с ходом и отменой; готово — <see cref="Finished"/>
/// с новым профилем, окно закрывается с ним. Сбой или отмена — форма возвращается, причина видна до следующей правки.
/// </summary>
public sealed partial class CloneProfileViewModel : ObservableObject
{
    /// <summary>Пауза перед пересчётом плана: план — обход всей папки данных, считаем не на каждую букву.</summary>
    public static readonly TimeSpan PlanDelay = TimeSpan.FromMilliseconds(300);

    private readonly GameProfile _source;
    private readonly CloneProfileServices _services;
    private readonly IUiTimer _delay;
    private bool _targetEdited;   // пользователь сам поправил папку — больше не подставляем её по названию
    private bool _settingTarget;  // папку подставляет модель — это не правка пользователя
    private int _generation;      // номер пересчёта: ответ старого пересчёта не показываем
    private string? _notice;      // итог прерванного копирования — держится до следующей правки
    private ClonePlan? _plan;
    private CancellationTokenSource? _copy;

    private bool _canGo;
    private bool _hasBackups;
    private string _backupsText = "";
    private string _summary = "";
    private string _errorText = "";
    private bool _isCopying;
    private double _progress;
    private string _progressText = "";

    /// <param name="source">Профиль-источник: его папка данных копируется (при импорте — папка существующих данных).</param>
    /// <param name="serverRunning">Сервер профиля запущен — копия мира может получиться битой, «Клонировать» недоступна.</param>
    /// <param name="importName">Не null — импорт существующих данных сервера в новый профиль с этим названием.</param>
    /// <param name="services">Диск, таймер и окна системы; по умолчанию — настоящие (тесты подменяют).</param>
    public CloneProfileViewModel(GameProfile source, bool serverRunning, string? importName = null, CloneProfileServices? services = null)
    {
        _source = source;
        _services = services ?? CloneProfileServices.Default;
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

        _name = importName ?? Loc.T("clone.copySuffix", source.Name);
        _worldName = _name;
        _targetDir = SuggestedTarget(_name) ?? "";

        _delay = _services.Timer(PlanDelay);
        _delay.Tick += (_, _) =>
        {
            _delay.Stop();
            Planning = RecalculateNowAsync();
        };

        // резервные копии бывают большими — размер считаем в фоне
        BackupsCounting = CountBackupsAsync();
        Planning = RecalculateNowAsync();
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

    /// <summary>Название нового профиля (как ввели; в план идёт без пробелов по краям).</summary>
    [ObservableProperty] private string _name;

    /// <summary>Папка данных нового профиля. Пока её не правили руками, она следует за названием.</summary>
    [ObservableProperty] private string _targetDir;

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

    partial void OnNameChanged(string value)
    {
        if (!_targetEdited && SuggestedTarget(value) is { } target)
        {
            _settingTarget = true;
            try { TargetDir = target; }
            finally { _settingTarget = false; }
        }
        Recalculate();
    }

    partial void OnTargetDirChanged(string value)
    {
        if (!_settingTarget) _targetEdited = true;
        Recalculate();
    }

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

    /// <summary>Что получится: сколько файлов и места, свободно на диске, откуда соберутся моды; что не копируется.</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>Почему нельзя (ошибка плана, нет места) или итог прерванного копирования; пусто — всё в порядке.</summary>
    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => ErrorText.Length > 0;

    /// <summary>План готов, места хватает и сервер не запущен — «Клонировать» доступна.</summary>
    public bool CanGo
    {
        get => _canGo;
        private set
        {
            if (SetProperty(ref _canGo, value)) GoCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Идёт копирование: форма выключена, виден ход. После удачного копирования не сбрасывается — окно закрывается с
    /// готовым профилем; после сбоя или отмены — сбрасывается, форма возвращается.
    /// </summary>
    public bool IsCopying
    {
        get => _isCopying;
        private set
        {
            if (SetProperty(ref _isCopying, value)) OnPropertyChanged(nameof(IsFormEnabled));
        }
    }

    /// <summary>Форму можно править (не идёт копирование).</summary>
    public bool IsFormEnabled => !IsCopying;

    /// <summary>Ход копирования — доля 0..1.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    /// <summary>«Копирую … 1,2 МБ из 3,4 МБ».</summary>
    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    /// <summary>Готовый профиль (папка уже скопирована); null — ещё не готов.</summary>
    public GameProfile? Result { get; private set; }

    /// <summary>Копирование закончилось удачно — окно закрывается с этим профилем.</summary>
    public event Action<GameProfile>? Finished;

    /// <summary>Идущий (или последний) пересчёт плана. Окно его не ждёт; тесты ждут, чтобы проверить итог.</summary>
    public Task Planning { get; private set; }

    /// <summary>Подсчёт размера резервных копий (идёт в фоне с открытия окна).</summary>
    public Task BackupsCounting { get; }

    private static string Clean(string? text) => text?.Trim() ?? "";

    /// <summary>Папка по названию («…\ServerProfiles\название»); у источника нет папки данных — подставлять не от чего (null).</summary>
    private string? SuggestedTarget(string name) =>
        string.IsNullOrWhiteSpace(_source.DataDir) ? null : ProfileCloner.SuggestTargetDir(_source.DataDir, name);

    private CloneOptions Options() => new()
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

    /// <summary>Сколько занимают Backups и BackupSaves в папке данных (недоступное не считается).</summary>
    public static long BackupBytes(string? dataDir)
    {
        // без папки данных — ноль, а не «Backups» в текущей папке программы
        if (string.IsNullOrWhiteSpace(dataDir)) return 0;
        long sum = 0;
        foreach (var name in new[] { "Backups", "BackupSaves" })
        {
            try
            {
                var dir = Path.Combine(dataDir, name);
                if (Directory.Exists(dir))
                    sum += new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return sum;
    }

    private async Task CountBackupsAsync()
    {
        var dataDir = _source.DataDir;
        var bytes = await Task.Run(() => _services.BackupBytes(dataDir));
        if (bytes <= 0) return;
        BackupsText = Loc.T("clone.backups", Sizes.Format(bytes));
        HasBackups = true;
    }

    /// <summary>Что-то поменяли: прежний план не годится, новый посчитаем после паузы.</summary>
    private void Recalculate()
    {
        _notice = null;
        _plan = null;
        _generation++;
        CanGo = false;
        _delay.Stop();
        _delay.Start();
    }

    /// <summary>Пересчитать план в фоне; устаревший ответ (условия успели поменяться) отбрасывается.</summary>
    private async Task RecalculateNowAsync()
    {
        if (_copy is not null) return;
        var generation = ++_generation;
        var options = Options();
        _plan = null;
        CanGo = false;

        ClonePlan? plan = null;
        string? error = null;
        try
        {
            plan = await Task.Run(() => _services.Plan(_source, options));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or Newtonsoft.Json.JsonException) // битый конфиг — тоже понятная причина
        {
            error = ex.Message;
        }
        catch (Exception ex)
        {
            // неожиданное: в журнал, а в окне — причина, иначе «Клонировать» молча осталась бы выключенной
            _services.LogError(ex);
            error = ex.Message;
        }
        if (generation != _generation) return;

        if (plan is null)
        {
            Summary = Loc.T("clone.summarySkipped");
            ErrorText = _notice ?? error ?? "";
            return;
        }

        var free = _services.FreeSpace(options.TargetDir);
        Summary = Loc.T("clone.summary", plan.Files.Count, Sizes.Format(plan.TotalBytes), free is { } f ? Sizes.Format(f) : "—")
                  + ProfileCopyText.ModsText(plan.ModSources, plan.ModNotes)
                  + " " + Loc.T("clone.summarySkipped");
        var noSpace = free is { } available && available < plan.TotalBytes;
        ErrorText = noSpace ? Loc.T("clone.noSpace") : _notice ?? "";
        _plan = plan;
        CanGo = !noSpace && !IsServerRunning;
    }

    /// <summary>«Клонировать»: скопировать по готовому плану с ходом; готово — <see cref="Finished"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanGo))]
    private async Task GoAsync()
    {
        if (_plan is not { } ready || _copy is not null) return;
        // название мира на состав файлов не влияет — берём то, что в поле сейчас
        var plan = ready with { Options = ready.Options with { WorldName = Clean(WorldName) } };

        var copy = _copy = new CancellationTokenSource();
        IsCopying = true;
        CanGo = false;
        ErrorText = "";
        Progress = 0;
        ProgressText = "";
        var total = Math.Max(1, plan.TotalBytes);
        var progress = new Progress<CloneProgress>(p =>
        {
            Progress = (double)p.DoneBytes / total;
            ProgressText = Loc.T("clone.copying", p.File, Sizes.Format(p.DoneBytes), Sizes.Format(p.TotalBytes));
        });

        GameProfile result;
        try
        {
            result = await Task.Run(() => _services.Apply(plan, progress, copy.Token));
        }
        catch (OperationCanceledException)
        {
            Finish(Loc.T("clone.cancelled"));
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException
                                       or InvalidOperationException) // в папке назначения что-то появилось — копия не поставлена
        {
            Finish(Loc.T("clone.failed", ex.Message));
            return;
        }
        catch (Exception ex)
        {
            // неожиданное: без этого окно осталось бы с выключенной формой и больше не закрылось бы (копирование
            // «идёт» навсегда) — возвращаем форму, подробности в журнал
            _services.LogError(ex);
            Finish(Loc.T("clone.failed", ex.Message));
            return;
        }

        _copy = null;
        copy.Dispose();
        Result = result;
        Finished?.Invoke(result);
    }

    /// <summary>Копирование прервано: вернуть форму, показать причину (она держится до следующей правки).</summary>
    private void Finish(string message)
    {
        _copy?.Dispose();
        _copy = null;
        IsCopying = false;
        _notice = message;
        Planning = RecalculateNowAsync();
    }

    /// <summary>
    /// «Отмена» или закрытие окна во время копирования: прервать его — окно остаётся, пока копирование не остановится и
    /// не уберёт за собой. true — копирование шло (окно не закрывать); false — не шло, окно можно закрыть.
    /// </summary>
    public bool CancelCopy()
    {
        if (_copy is not { } copy) return false;
        copy.Cancel();
        return true;
    }

    /// <summary>«Обзор…» у папки нового профиля: начать с ближайшей существующей папки из поля.</summary>
    [RelayCommand]
    private void Browse()
    {
        var start = Clean(TargetDir);
        while (start.Length > 0 && !Directory.Exists(start)) start = Path.GetDirectoryName(start) ?? "";
        if (_services.PickFolder(Loc.T("clone.pickTarget"), start) is { } dir) TargetDir = dir;
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
/// <see cref="Finished"/> с новым профилем (папка уже создана), окно закрывается с ним.
/// </summary>
public sealed partial class ClientProfileViewModel : ObservableObject
{
    /// <summary>План — это обход папки данных: считаем не на каждую букву, а после короткой паузы.</summary>
    public static readonly TimeSpan RecalculateDelay = TimeSpan.FromMilliseconds(300);

    private readonly GameProfile _source;
    private readonly bool _clone;
    private readonly ClientProfileServices _services;
    private readonly string _home;     // от неё предлагается папка нового профиля (…\ClientProfiles\имя)
    private readonly IUiTimer _delay;
    private bool _targetEdited;        // папку поправили руками — больше не подставляем её по названию
    private bool _settingTarget;
    private int _generation;           // номер расчёта: ответ старого расчёта (условия успели поменяться) не показываем
    private string? _notice;           // итог прерванного копирования — держится до следующей правки
    private ClientClonePlan? _plan;
    private CancellationTokenSource? _copy;
    private bool _canGo;
    private bool _isCopying;
    private bool _isGameRunning;
    private string _summary = "";
    private string _errorText = "";
    private double _progressValue;
    private string _progressText = "";
    private bool _hasWorlds;
    private string _worldsText = "";

    /// <param name="source">Профиль-основа.</param>
    /// <param name="clone">true — «Клонировать…» (миры по умолчанию копируются), false — «+ Клиент».</param>
    /// <param name="suggestedName">Название нового профиля по умолчанию.</param>
    /// <param name="services">Система (тесты подменяют); null — настоящая.</param>
    public ClientProfileViewModel(GameProfile source, bool clone, string suggestedName, ClientProfileServices? services = null)
    {
        _source = source;
        _clone = clone;
        _services = services ?? new ClientProfileServices();
        _home = string.IsNullOrWhiteSpace(source.DataDir) ? _services.DefaultDataDir ?? GameInstall.DefaultDataDir : source.DataDir;
        _delay = Ui.Timer(RecalculateDelay);
        _delay.Tick += (_, _) =>
        {
            _delay.Stop();
            RecalculateNow();
        };

        Title = Loc.T(clone ? "clone.title" : "newprofile.titleClient");
        Heading = clone ? Loc.T("clone.heading", source.Name) : Loc.T("newprofile.titleClient");
        Intro = clone ? Loc.T("cprofile.introClone") : Loc.T("cprofile.introNew", source.Name);
        GoText = Loc.T(clone ? "clone.go" : "newprofile.create");

        var modDirs = ProfileResolver.Resolve(source).ModDirs;
        SharedModsHint = Loc.T("cprofile.modsSharedHint", source.Name, modDirs.Count == 0 ? "—" : string.Join("; ", modDirs));
        // у основы модов нет вовсе (игра ещё не запускалась) — делить и копировать нечего: только с нуля
        HasMods = modDirs.Count > 0;
        _mods = HasMods ? ClientModsMode.Shared : ClientModsMode.Empty;

        _name = suggestedName;
        _targetDir = SuggestTarget(suggestedName);
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

    /// <summary>Название профиля (как ввели; в план идёт без пробелов по краям).</summary>
    [ObservableProperty] private string _name;

    /// <summary>Папка данных нового профиля. Пока её не правили руками, подставляется по названию.</summary>
    [ObservableProperty] private string _targetDir;

    [ObservableProperty] private ClientModsMode _mods;

    /// <summary>Настройки игры — как у основы (true) или чистые (false).</summary>
    [ObservableProperty] private bool _copySettings = true;

    /// <summary>Копировать миры одиночной игры и карты (галочка видна, только если в основе миры есть).</summary>
    [ObservableProperty] private bool _copyWorlds;

    partial void OnNameChanged(string value)
    {
        if (!_targetEdited)
        {
            _settingTarget = true;
            TargetDir = SuggestTarget(value);
            _settingTarget = false;
        }
        Recalculate();
    }

    partial void OnTargetDirChanged(string value)
    {
        if (!_settingTarget) _targetEdited = true;
        Recalculate();
    }

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

    /// <summary>Что получится: сколько файлов и места, свободно на диске, откуда моды, что не копируется.</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>Почему нельзя (ошибка плана, нет места) или чем кончилось прерванное копирование; пусто — всё в порядке.</summary>
    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    /// <summary>План готов, место есть, игра не мешает — главная кнопка доступна.</summary>
    public bool CanGo
    {
        get => _canGo;
        private set
        {
            if (SetProperty(ref _canGo, value)) GoCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Идёт копирование: форма выключена, виден его ход.</summary>
    public bool IsCopying
    {
        get => _isCopying;
        private set => SetProperty(ref _isCopying, value);
    }

    /// <summary>Ход копирования: доля 0..1.</summary>
    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    /// <summary>«Копирую …… 12 МБ из 1,2 ГБ».</summary>
    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    /// <summary>
    /// Последний начатый расчёт плана (окну не нужен: по нему тесты ждут ответа). Неожиданная ошибка расчёта уходит и
    /// в обработчик сбоев программы, как из async void в окне на WPF.
    /// </summary>
    public Task Calculation { get; private set; } = Task.CompletedTask;

    /// <summary>Готовый профиль (папка уже создана); null — ещё не создан.</summary>
    public GameProfile? Result { get; private set; }

    /// <summary>Профиль создан — окно закрывается с ним.</summary>
    public event Action<GameProfile>? Finished;

    private string SuggestTarget(string? name) =>
        (name ?? "").Trim().Length == 0 ? "" : ClientProfileLayout.SuggestDir(_home, name!);

    private ClientCloneOptions Options => new()
    {
        Name = (Name ?? "").Trim(),
        TargetDir = (TargetDir ?? "").Trim(),
        Mods = Mods,
        CopySettings = CopySettings,
        CopyWorlds = CopyWorlds,
    };

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
    public static long WorldBytes(string? dataDir)
    {
        if (string.IsNullOrWhiteSpace(dataDir)) return 0;
        long sum = 0;
        foreach (var dir in ClientProfileCloner.WorldFolders(dataDir))
        {
            try
            {
                sum += new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return sum;
    }

    /// <summary>«Обзор…» у папки данных: начать с ближайшей существующей папки над введённой.</summary>
    [RelayCommand]
    private void Browse()
    {
        var start = (TargetDir ?? "").Trim();
        while (start.Length > 0 && !Directory.Exists(start)) start = Path.GetDirectoryName(start) ?? "";
        if (Ui.Platform.PickFolder(Loc.T("clone.pickTarget"), start) is { } dir) TargetDir = dir;
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

    private void RecalculateNow()
    {
        Calculation = RecalculateAsync();
        Observe(Calculation);
    }

    // как async void в окне на WPF: неожиданная ошибка не теряется, а идёт в обработчик сбоев программы
    private static async void Observe(Task task) => await task;

    /// <summary>Пересчитать план в фоне; устаревший ответ (условия успели поменяться) отбрасывается.</summary>
    private async Task RecalculateAsync()
    {
        if (_copy is not null) return;
        var generation = ++_generation;
        var options = Options;
        _plan = null;
        CanGo = false;

        ClientClonePlan? plan = null;
        string? error = null;
        var running = false;
        try
        {
            (plan, running) = await Task.Run(() => (_services.Plan(_source, options), options.CopyWorlds && _services.IsGameRunning(_source)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or Newtonsoft.Json.JsonException)
        {
            error = ex.Message;
        }
        if (generation != _generation) return;

        // открытый в игре мир копировать нельзя; без миров запущенная игра не мешает
        IsGameRunning = running;
        if (plan is null)
        {
            Summary = Loc.T("cprofile.summarySkipped");
            ShowError(_notice ?? error);
            return;
        }

        var free = _services.FreeSpace(options.TargetDir);
        Summary = Loc.T("clone.summary", plan.Files.Count, Sizes.Format(plan.TotalBytes), free is { } f ? Sizes.Format(f) : "—")
                  + ProfileCopyText.ModsText(plan.ModSources, plan.ModNotes)
                  + " " + Loc.T("cprofile.summarySkipped");
        var noSpace = free is { } available && available < plan.TotalBytes;
        ShowError(noSpace ? Loc.T("clone.noSpace") : _notice);
        _plan = plan;
        CanGo = !noSpace && !running;
    }

    private void ShowError(string? text) => ErrorText = text ?? "";

    /// <summary>«Создать» / «Клонировать»: скопировать по плану с ходом; отменили или не вышло — форма возвращается с причиной.</summary>
    [RelayCommand(CanExecute = nameof(CanGo))]
    private async Task GoAsync()
    {
        if (_plan is not { } plan || _copy is not null) return;

        _copy = new CancellationTokenSource();
        var token = _copy.Token;
        IsCopying = true;
        CanGo = false;
        ShowError(null);
        ProgressValue = 0;
        ProgressText = "";
        var total = Math.Max(1, plan.TotalBytes);
        var progress = new Progress<CloneProgress>(p =>
        {
            ProgressValue = (double)p.DoneBytes / total;
            ProgressText = Loc.T("clone.copying", p.File, Sizes.Format(p.DoneBytes), Sizes.Format(p.TotalBytes));
        });

        try
        {
            var created = await Task.Run(() => _services.Apply(plan, progress, token));
            _copy = null;
            Result = created;
            Finished?.Invoke(created);
        }
        catch (OperationCanceledException)
        {
            Finish(Loc.T("clone.cancelled"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException
                                       or InvalidOperationException) // в папке назначения что-то появилось — копия не поставлена
        {
            Finish(Loc.T("clone.failed", ex.Message));
        }
        catch (Exception ex)
        {
            // неожиданное: без этого окно осталось бы с выключенной формой и больше не закрылось бы (копирование
            // «идёт» навсегда) — возвращаем форму, подробности в журнал
            Ui.Platform.WriteCrashLog(ex);
            Finish(Loc.T("clone.failed", ex.Message));
        }
    }

    /// <summary>Копирование прервано: вернуть форму, показать причину (до следующей правки), пересчитать план.</summary>
    private void Finish(string message)
    {
        _copy?.Dispose();
        _copy = null;
        IsCopying = false;
        _notice = message;
        RecalculateNow();
    }

    /// <summary>
    /// «Отмена», Escape или крестик. Идёт копирование — прервать его (окно остаётся, дождётся, пока копирование
    /// остановится и уберёт за собой): true. Не идёт — false, окно можно закрывать.
    /// </summary>
    public bool CancelCopy()
    {
        if (_copy is null) return false;
        _copy.Cancel();
        return true;
    }
}

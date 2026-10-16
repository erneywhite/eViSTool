using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Общее у окон, которые копируют профиль в новую папку данных: нового клиентского профиля или его клона
/// (<see cref="ClientProfileViewModel"/>) и клона серверного профиля (<see cref="CloneProfileViewModel"/>).
/// Здесь название и папка (папка следует за названием, пока её не поправили руками), «Обзор…», пересчёт плана в фоне
/// после паузы в правке, сводка со свободным местом, копирование с ходом и отменой. Устаревший ответ расчёта (условия
/// успели поменяться) отбрасывается; причина прерванного копирования держится до следующей правки. Какие ещё поля есть
/// в окне, как из них собрать условия, посчитать план и скопировать — решает наследник.
/// </summary>
/// <typeparam name="TOptions">Условия плана, собранные с полей окна.</typeparam>
/// <typeparam name="TPlan">План копирования: что и куда копируется.</typeparam>
public abstract partial class ProfileCopyViewModel<TOptions, TPlan> : ObservableObject where TPlan : class
{
    private readonly IUiTimer _delay;
    private bool _targetEdited;   // папку поправили руками — больше не подставляем её по названию
    private bool _settingTarget;  // папку подставляет модель — это не правка пользователя
    private int _generation;      // номер расчёта: ответ старого расчёта (условия успели поменяться) не показываем
    private string? _notice;      // итог прерванного копирования — держится до следующей правки
    private TPlan? _plan;
    private CancellationTokenSource? _copy;
    private bool _canGo;
    private bool _isCopying;
    private string _summary = "";
    private string _errorText = "";
    private string _progressText = "";

    /// <summary>Наследники — только окна этой сборки (поэтому private protected).</summary>
    /// <param name="delay">Таймер паузы перед пересчётом плана: наследник берёт его у системы (тесты — свой, ручной).</param>
    private protected ProfileCopyViewModel(IUiTimer delay)
    {
        _delay = delay;
        _delay.Tick += (_, _) =>
        {
            _delay.Stop();
            RecalculateNow();
        };
    }

    /// <summary>Название нового профиля (как ввели; в план идёт без пробелов по краям).</summary>
    [ObservableProperty] private string _name = "";

    /// <summary>Папка данных нового профиля. Пока её не правили руками, она следует за названием.</summary>
    [ObservableProperty] private string _targetDir = "";

    partial void OnNameChanged(string value)
    {
        if (!_targetEdited && SuggestTarget(value) is { } target)
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

    /// <summary>Что получится: сколько файлов и места, свободно на диске, откуда соберутся моды, что не копируется.</summary>
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

    /// <summary>План готов, места хватает, копированию ничего не мешает — главная кнопка доступна.</summary>
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
        private set => SetProperty(ref _isCopying, value);
    }

    /// <summary>«Копирую … 12 МБ из 1,2 ГБ».</summary>
    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    /// <summary>Готовый профиль (папка уже создана); null — ещё не готов.</summary>
    public GameProfile? Result { get; private set; }

    /// <summary>Копирование закончилось удачно — окно закрывается с этим профилем.</summary>
    public event Action<GameProfile>? Finished;

    /// <summary>Последний начатый расчёт плана. Окно его не ждёт; тесты ждут (у каждого окна своё имя для него).</summary>
    protected Task LastCalculation { get; private set; } = Task.CompletedTask;

    /// <summary>Что из плана нужно сводке и ходу копирования.</summary>
    /// <param name="Files">Сколько файлов копируется.</param>
    /// <param name="Bytes">Сколько они весят.</param>
    /// <param name="ModSources">Своя копия модов: из каких папок она соберётся.</param>
    /// <param name="ModNotes">Что при сборке модов пошло не один к одному.</param>
    protected readonly record struct PlanFacts(int Files, long Bytes, IReadOnlyList<string> ModSources, IReadOnlyList<string> ModNotes);

    // ---- что решает наследник

    /// <summary>Папка по названию; null — подставлять не от чего, папку не трогаем.</summary>
    protected abstract string? SuggestTarget(string name);

    /// <summary>Условия плана с полей окна (в потоке окна, перед уходом расчёта в фон).</summary>
    protected abstract TOptions ReadOptions();

    /// <summary>
    /// План по условиям — в фоновом потоке. Blocked — план есть, но копировать сейчас нельзя (запущена игра или
    /// сервер). Создать профиль нельзя — исключение с понятным текстом (InvalidOperationException и т. п.).
    /// </summary>
    protected abstract (TPlan Plan, bool Blocked) MakePlan(TOptions options);

    /// <summary>Сколько в плане файлов и места, откуда соберутся моды.</summary>
    protected abstract PlanFacts Facts(TPlan plan);

    /// <summary>Что не копируется никогда: вся сводка, если плана нет, и её хвост, если он есть.</summary>
    protected abstract string SkippedText { get; }

    /// <summary>
    /// Неожиданная ошибка расчёта плана (не из понятных причин вроде битого конфига): true — в журнал программы и
    /// причиной в окне, форма остаётся живой; false — дальше, в обработчик сбоев программы.
    /// </summary>
    protected abstract bool ShowsUnexpectedPlanErrors { get; }

    /// <summary>Свободное место на диске папки; не узнать — null.</summary>
    protected abstract long? FreeSpace(string dir);

    /// <summary>Скопировать по плану (идёт в фоновом потоке): ход — в progress, готовый профиль — ответ.</summary>
    protected abstract Task<GameProfile> Apply(TPlan plan, IProgress<CloneProgress> progress, CancellationToken ct);

    /// <summary>Выбор папки («Обзор…»): заголовок, с какой папки начать; отменили — null.</summary>
    protected abstract string? PickFolder(string title, string start);

    /// <summary>Неожиданная ошибка — в журнал программы.</summary>
    protected abstract void LogError(Exception ex);

    /// <summary>Ход копирования, доля 0..1. Свойство с ней окна называют по-своему, поэтому показывает наследник.</summary>
    protected abstract void ShowProgress(double share);

    /// <summary>Ответ расчёта принят (не устарел): мешает ли что-то копированию. План не составился — false.</summary>
    protected virtual void ShowBlocked(bool blocked) { }

    /// <summary>План перед самым копированием: то, что на состав файлов не влияет, можно взять с формы сейчас.</summary>
    protected virtual TPlan BeforeCopy(TPlan plan) => plan;

    // ---- общее

    protected static string Clean(string? text) => text?.Trim() ?? "";

    /// <summary>Начальные название и папка (наследник задаёт их в конструкторе) — без паузы перед пересчётом.</summary>
    protected void SetInitial(string name, string targetDir)
    {
#pragma warning disable MVVMTK0034 // намеренно мимо свойств: их обработчики запустили бы паузу перед пересчётом
        _name = name;
        _targetDir = targetDir;
#pragma warning restore MVVMTK0034
    }

    /// <summary>Что-то поменяли: прежний план не годится, новый посчитаем после паузы.</summary>
    protected void Recalculate()
    {
        _notice = null;
        _plan = null;
        _generation++;
        CanGo = false;
        _delay.Stop();
        _delay.Start();
    }

    /// <summary>Посчитать план сразу, без паузы (при открытии окна и после прерванного копирования).</summary>
    protected void RecalculateNow()
    {
        LastCalculation = RecalculateAsync();
        if (!ShowsUnexpectedPlanErrors) Observe(LastCalculation);
    }

    // как async void: неожиданная ошибка не теряется, а идёт в обработчик сбоев программы
    private static async void Observe(Task task) => await task;

    /// <summary>Пересчитать план в фоне; устаревший ответ (условия успели поменяться) отбрасывается.</summary>
    private async Task RecalculateAsync()
    {
        if (_copy is not null) return;
        var generation = ++_generation;
        var options = ReadOptions();
        var target = Clean(TargetDir);
        _plan = null;
        CanGo = false;

        TPlan? plan = null;
        var blocked = false;
        string? error = null;
        try
        {
            (plan, blocked) = await Task.Run(() => MakePlan(options));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or Newtonsoft.Json.JsonException) // битый конфиг — тоже понятная причина
        {
            error = ex.Message;
        }
        catch (Exception ex) when (ShowsUnexpectedPlanErrors)
        {
            // неожиданное: в журнал, а в окне — причина, иначе главная кнопка молча осталась бы выключенной
            LogError(ex);
            error = ex.Message;
        }
        if (generation != _generation) return;

        // например, открытый в игре мир копировать нельзя
        ShowBlocked(blocked);
        if (plan is null)
        {
            Summary = SkippedText;
            ErrorText = _notice ?? error ?? "";
            return;
        }

        var facts = Facts(plan);
        var free = FreeSpace(target);
        Summary = Loc.T("clone.summary", facts.Files, Sizes.Format(facts.Bytes), free is { } f ? Sizes.Format(f) : "—")
                  + ProfileCopyText.ModsText(facts.ModSources, facts.ModNotes)
                  + " " + SkippedText;
        var noSpace = free is { } available && available < facts.Bytes;
        ErrorText = noSpace ? Loc.T("clone.noSpace") : _notice ?? "";
        _plan = plan;
        CanGo = !noSpace && !blocked;
    }

    /// <summary>Главная кнопка: скопировать по готовому плану с ходом; готово — <see cref="Finished"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanGo))]
    private async Task GoAsync()
    {
        if (_plan is not { } ready || _copy is not null) return;
        var plan = BeforeCopy(ready);

        var copy = _copy = new CancellationTokenSource();
        IsCopying = true;
        CanGo = false;
        ErrorText = "";
        ShowProgress(0);
        ProgressText = "";
        var total = Math.Max(1, Facts(plan).Bytes);
        var progress = new Progress<CloneProgress>(p =>
        {
            ShowProgress((double)p.DoneBytes / total);
            ProgressText = Loc.T("clone.copying", p.File, Sizes.Format(p.DoneBytes), Sizes.Format(p.TotalBytes));
        });

        GameProfile created;
        try
        {
            created = await Task.Run(() => Apply(plan, progress, copy.Token));
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
            LogError(ex);
            Finish(Loc.T("clone.failed", ex.Message));
            return;
        }

        _copy = null;
        copy.Dispose();
        Result = created;
        Finished?.Invoke(created);
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
    /// «Отмена», Escape или крестик во время копирования: прервать его — окно остаётся, пока копирование не
    /// остановится и не уберёт за собой. true — копирование шло (окно не закрывать); false — не шло, окно можно закрыть.
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
        if (PickFolder(Loc.T("clone.pickTarget"), start) is { } dir) TargetDir = dir;
    }
}

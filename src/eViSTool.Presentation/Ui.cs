namespace eViSTool.Presentation;

/// <summary>Какие кнопки у вопроса.</summary>
public enum AskButtons { YesNo, YesNoCancel, OkCancel }

/// <summary>Что ответили. Закрыли окно вопроса крестиком — Cancel (или No, если «Отмены» не было).</summary>
public enum Answer { Yes, No, Cancel, Ok }

/// <summary>Вид сообщения: сведение, предупреждение, ошибка.</summary>
public enum NoticeKind { Info, Warning, Error }

/// <summary>Фильтр в окне выбора файла: «Архивы модов» → *.zip.</summary>
public sealed record FileFilter(string Name, params string[] Patterns);

/// <summary>Таймер в потоке окна: Tick приходит туда же, где живут модели.</summary>
public interface IUiTimer
{
    TimeSpan Interval { get; set; }
    bool IsEnabled { get; }
    event EventHandler? Tick;
    void Start();
    void Stop();
}

/// <summary>
/// Что моделям нужно от оконного движка и системы. Реализация — у окна (WPF сейчас, Avalonia потом); в тестах —
/// своя, с заранее заданными ответами.
/// </summary>
public interface IUiPlatform
{
    // ---- вопросы и сообщения (владелец — главное окно)
    /// <param name="byDefault">Кнопка по умолчанию (Enter); null — первая. У опасных вопросов — «Нет».</param>
    /// <param name="warning">Вопрос-предупреждение (жёлтый значок вместо вопросительного).</param>
    Answer Ask(string text, AskButtons buttons, Answer? byDefault = null, bool warning = false);
    void Notify(string text, NoticeKind kind);

    // ---- поток окна
    bool IsUiThread { get; }
    /// <summary>Выполнить в потоке окна, не дожидаясь.</summary>
    void Post(Action action);
    /// <summary>Выполнить в потоке окна, когда оно освободится (после отрисовки и ввода).</summary>
    void PostIdle(Action action);
    /// <summary>Выполнить в потоке окна и дождаться.</summary>
    void Invoke(Action action);
    IUiTimer CreateTimer(TimeSpan interval);

    // ---- выбор файлов и папок; null — отменили
    IReadOnlyList<string>? PickFiles(string title, IReadOnlyList<FileFilter> filters, bool multiple);
    /// <param name="confirmOverwrite">Спросить, если файл уже есть (выбор пути к будущему файлу — без вопроса).</param>
    string? PickSaveFile(string title, IReadOnlyList<FileFilter> filters, string suggestedName, string? folder = null, bool confirmOverwrite = true);
    string? PickFolder(string title, string? initialFolder);

    // ---- система
    void SetClipboard(string text);
    void OpenUrl(string url);
    void OpenFolder(string dir);
    /// <summary>Открыть папку и выделить в ней файл.</summary>
    void ShowInFolder(string path);
    /// <summary>В корзину (файл или папку).</summary>
    void MoveToTrash(string path);

    // ---- программа
    /// <summary>Главное окно на экране (не закрыто и не закрывается).</summary>
    bool IsMainWindowVisible { get; }
    /// <summary>Закрыть главное окно и запустить exe, когда программа выйдет; закрытие отменили — ничего.</summary>
    void RestartAfterClose(string exe);
    /// <summary>Записать ошибку в журнал программы; возвращает путь к журналу.</summary>
    string WriteCrashLog(Exception ex);
}

/// <summary>
/// Доступ моделей к платформе. Окно ставит свою реализацию при запуске (<see cref="Platform"/>), тесты — свою.
/// Короткие методы — для частых вызовов: <c>Ui.Ask(...)</c> читается проще, чем <c>Ui.Platform.Ask(...)</c>.
/// </summary>
public static class Ui
{
    private static IUiPlatform? _platform;

    /// <summary>Платформа уже задана (тесты ставят свою, только если окно не поставило).</summary>
    public static bool IsSet => _platform is not null;

    public static IUiPlatform Platform
    {
        get => _platform ?? throw new InvalidOperationException("Ui.Platform не задан: окно ставит его при запуске, тест — перед проверкой.");
        set => _platform = value;
    }

    public static Answer Ask(string text, AskButtons buttons = AskButtons.YesNo, Answer? byDefault = null, bool warning = false) =>
        Platform.Ask(text, buttons, byDefault, warning);

    /// <summary>«Да» на вопрос «да / нет».</summary>
    public static bool Confirm(string text, Answer? byDefault = null, bool warning = false) =>
        Platform.Ask(text, AskButtons.YesNo, byDefault, warning) == Answer.Yes;

    public static void Info(string text) => Platform.Notify(text, NoticeKind.Info);
    public static void Warn(string text) => Platform.Notify(text, NoticeKind.Warning);
    public static void Error(string text) => Platform.Notify(text, NoticeKind.Error);

    public static void Post(Action action) => Platform.Post(action);
    public static void PostIdle(Action action) => Platform.PostIdle(action);
    public static void Invoke(Action action) => Platform.Invoke(action);
    public static IUiTimer Timer(TimeSpan interval) => Platform.CreateTimer(interval);

    public static void OpenUrl(string url) => Platform.OpenUrl(url);
    public static void OpenFolder(string dir) => Platform.OpenFolder(dir);
    public static void ShowInFolder(string path) => Platform.ShowInFolder(path);
}

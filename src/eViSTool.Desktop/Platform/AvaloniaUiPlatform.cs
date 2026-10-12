using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using eViSTool.Presentation;

namespace eViSTool.Desktop.Platform;

/// <summary>
/// Платформа моделей (<see cref="Ui"/>) для окна на Avalonia — одна на Windows и Linux. Вопросы и выбор файлов в
/// Avalonia асинхронные; модели ждут ответа на месте, поэтому ожидание — через вложенный цикл диспетчера (<see cref="Modal"/>).
/// </summary>
public class AvaloniaUiPlatform : IUiPlatform
{
    /// <summary>Главное окно, если оно на экране: владелец вопросов и диалогов.</summary>
    internal static Window? MainWindow =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { IsVisible: true } main } ? main : null;

    private static T OnUiThread<T>(Func<T> action) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.Invoke(action);

    public virtual Answer Ask(string text, AskButtons buttons, Answer? byDefault = null, bool warning = false) =>
        OnUiThread(() => Modal.Wait(() => MessageDialog.AskAsync(Owner, text, buttons, byDefault, warning)));

    public virtual void Notify(string text, NoticeKind kind) =>
        OnUiThread(() => Modal.Wait(() => MessageDialog.NotifyAsync(Owner, text, kind)));

    public bool IsUiThread => Dispatcher.UIThread.CheckAccess();
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
    public void PostIdle(Action action) => Dispatcher.UIThread.Post(action, DispatcherPriority.ApplicationIdle);

    public void Invoke(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Invoke(action);
    }

    public IUiTimer CreateTimer(TimeSpan interval) => new Timer(interval);

    private sealed class Timer : IUiTimer
    {
        private readonly DispatcherTimer _timer;

        public Timer(TimeSpan interval)
        {
            _timer = new DispatcherTimer { Interval = interval };
            _timer.Tick += (_, e) => Tick?.Invoke(this, e);
        }

        public TimeSpan Interval { get => _timer.Interval; set => _timer.Interval = value; }
        public bool IsEnabled => _timer.IsEnabled;
        public event EventHandler? Tick;
        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
    }

    // ---- выбор файлов и папок: StorageProvider главного окна

    private static IStorageProvider? Storage => Owner?.StorageProvider;

    /// <summary>Чьим окном открыть вопрос или выбор файла: верхнее видимое (открытый диалог), иначе главное.</summary>
    private static Window? Owner => TopWindow() ?? MainWindow;

    /// <summary>Самое верхнее из видимых окон (у диалогов поверх диалогов — владелец следующего).</summary>
    internal static Window? TopWindow() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop ? desktop.Windows.LastOrDefault(w => w.IsVisible) : null;

    private static List<FilePickerFileType> Types(IReadOnlyList<FileFilter> filters) =>
        [.. filters.Select(f => new FilePickerFileType($"{f.Name} ({string.Join(";", f.Patterns)})") { Patterns = f.Patterns })];

    private static async Task<IStorageFolder?> FolderAt(IStorageProvider storage, string? folder) =>
        folder is not null && Directory.Exists(folder) ? await storage.TryGetFolderFromPathAsync(folder) : null;

    public IReadOnlyList<string>? PickFiles(string title, IReadOnlyList<FileFilter> filters, bool multiple) =>
        OnUiThread(() => Modal.Wait(async () =>
        {
            if (Storage is not { } storage) return null;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = multiple, FileTypeFilter = Types(filters) });
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            return paths.Count > 0 ? (IReadOnlyList<string>)paths : null;
        }));

    public string? PickSaveFile(string title, IReadOnlyList<FileFilter> filters, string suggestedName, string? folder = null, bool confirmOverwrite = true) =>
        OnUiThread(() => Modal.Wait(async () =>
        {
            if (Storage is not { } storage) return null;
            // расширение — из первого фильтра («*.evpack» → «evpack»)
            var ext = filters.FirstOrDefault()?.Patterns.FirstOrDefault() is { } first && first.StartsWith("*.") && first != "*.*" ? first[2..] : null;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                FileTypeChoices = Types(filters),
                DefaultExtension = ext,
                ShowOverwritePrompt = confirmOverwrite,
                SuggestedStartLocation = await FolderAt(storage, folder),
            });
            return file?.TryGetLocalPath();
        }));

    public string? PickFolder(string title, string? initialFolder) =>
        OnUiThread(() => Modal.Wait(async () =>
        {
            if (Storage is not { } storage) return null;
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = await FolderAt(storage, initialFolder),
            });
            return folders.FirstOrDefault()?.TryGetLocalPath();
        }));

    // ---- система

    public void SetClipboard(string text) =>
        OnUiThread(() => Modal.Wait(async () =>
        {
            if (Owner?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
            return true;
        }));

    public void OpenUrl(string url) => SystemShell.OpenUrl(url);
    public void OpenFolder(string dir) => SystemShell.OpenFolder(dir);
    public void ShowInFolder(string path) => SystemShell.ShowInFolder(path);
    public void MoveToTrash(string path) => SystemShell.MoveToTrash(path);
    public void OpenTrash() => SystemShell.OpenTrash();

    // ---- программа

    public bool IsMainWindowVisible => MainWindow is not null;
    public void RestartAfterClose(string exe) => App.RestartAfterClose(exe);
    public string WriteCrashLog(Exception ex) => CrashLog.Write(ex) ?? "";
}

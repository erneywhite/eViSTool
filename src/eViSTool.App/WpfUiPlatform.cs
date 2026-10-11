using System.IO;
using System.Windows;
using System.Windows.Threading;
using eViSTool.Presentation;
using Microsoft.Win32;

namespace eViSTool.App;

/// <summary>Платформа моделей (<see cref="Ui"/>) для окна на WPF: окна сообщений, Dispatcher, диалоги Windows, Shell.</summary>
public class WpfUiPlatform : IUiPlatform
{
    private static Dispatcher UiDispatcher => Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    /// <summary>Владелец окон сообщений: главное окно, если оно на экране (иначе сообщение без владельца).</summary>
    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } main ? main : null;

    public virtual Answer Ask(string text, AskButtons buttons, Answer? byDefault = null, bool warning = false)
    {
        var wpfButtons = buttons switch
        {
            AskButtons.YesNoCancel => MessageBoxButton.YesNoCancel,
            AskButtons.OkCancel => MessageBoxButton.OKCancel,
            _ => MessageBoxButton.YesNo,
        };
        var image = warning ? MessageBoxImage.Warning : MessageBoxImage.Question;
        var preset = byDefault switch
        {
            Answer.Yes => MessageBoxResult.Yes,
            Answer.No => MessageBoxResult.No,
            Answer.Cancel => MessageBoxResult.Cancel,
            Answer.Ok => MessageBoxResult.OK,
            _ => MessageBoxResult.None,
        };
        var result = Owner is { } owner
            ? MessageBox.Show(owner, text, "eViSTool", wpfButtons, image, preset)
            : MessageBox.Show(text, "eViSTool", wpfButtons, image, preset);
        return result switch
        {
            MessageBoxResult.Yes => Answer.Yes,
            MessageBoxResult.OK => Answer.Ok,
            MessageBoxResult.No => Answer.No,
            _ => buttons == AskButtons.YesNo ? Answer.No : Answer.Cancel,
        };
    }

    public virtual void Notify(string text, NoticeKind kind)
    {
        var image = kind switch
        {
            NoticeKind.Error => MessageBoxImage.Error,
            NoticeKind.Warning => MessageBoxImage.Warning,
            _ => MessageBoxImage.Information,
        };
        if (Owner is { } owner) MessageBox.Show(owner, text, "eViSTool", MessageBoxButton.OK, image);
        else MessageBox.Show(text, "eViSTool", MessageBoxButton.OK, image);
    }

    public bool IsUiThread => UiDispatcher.CheckAccess();
    public void Post(Action action) => UiDispatcher.BeginInvoke(action);
    public void PostIdle(Action action) => UiDispatcher.BeginInvoke(action, DispatcherPriority.ApplicationIdle);

    public void Invoke(Action action)
    {
        if (UiDispatcher.CheckAccess()) action();
        else UiDispatcher.Invoke(action);
    }

    public IUiTimer CreateTimer(TimeSpan interval) => new WpfTimer(interval);

    private sealed class WpfTimer : IUiTimer
    {
        private readonly DispatcherTimer _timer;

        public WpfTimer(TimeSpan interval)
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

    /// <summary>Фильтры в формате WPF: «Архивы (*.zip)|*.zip|…».</summary>
    private static string FilterText(IReadOnlyList<FileFilter> filters) =>
        string.Join("|", filters.Select(f => $"{f.Name} ({string.Join(";", f.Patterns)})|{string.Join(";", f.Patterns)}"));

    public IReadOnlyList<string>? PickFiles(string title, IReadOnlyList<FileFilter> filters, bool multiple)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = FilterText(filters), Multiselect = multiple };
        return dlg.ShowDialog(Owner) == true ? dlg.FileNames : null;
    }

    public string? PickSaveFile(string title, IReadOnlyList<FileFilter> filters, string suggestedName, string? folder = null,
        bool confirmOverwrite = true)
    {
        var dlg = new SaveFileDialog { Title = title, Filter = FilterText(filters), FileName = suggestedName, OverwritePrompt = confirmOverwrite };
        // расширение — из первого фильтра («*.evpack» → «.evpack»), если файл назван без него
        if (filters.FirstOrDefault()?.Patterns.FirstOrDefault() is { } first && first.StartsWith("*.") && first != "*.*")
        {
            dlg.DefaultExt = first[1..];
            dlg.AddExtension = true;
        }
        if (folder is not null && Directory.Exists(folder)) dlg.InitialDirectory = folder;
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public string? PickFolder(string title, string? initialFolder)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (initialFolder is not null && Directory.Exists(initialFolder)) dlg.InitialDirectory = initialFolder;
        return dlg.ShowDialog(Owner) == true ? dlg.FolderName : null;
    }

    public void SetClipboard(string text) => Clipboard.SetText(text);
    public void OpenUrl(string url) => Shell.OpenUrl(url);
    public void OpenFolder(string dir) => Shell.OpenFolder(dir);
    public void ShowInFolder(string path) => Shell.ShowInFolder(path);
    public void MoveToTrash(string path) => Shell.MoveToRecycleBin(path);
    public virtual void OpenTrash() => Shell.OpenRecycleBin();

    public bool IsMainWindowVisible => Application.Current?.MainWindow is { IsVisible: true };
    public void RestartAfterClose(string exe) => App.RestartAfterClose(exe);
    public string WriteCrashLog(Exception ex) => App.WriteCrashLog(ex) ?? "";
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using eViSTool.Desktop.Platform;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// Основа окон программы на Avalonia (диалоги и немодальные окна): значок, положение по центру владельца, Escape —
/// закрыть без ответа. От неё наследуют окна из .axaml: <c>&lt;dialogs:DialogWindow x:Class="…"&gt;</c>.
/// Модели спрашивают и ждут ответа на месте, — для этого <see cref="ShowModal{TResult}"/>:
/// возвращает то, что окно передало в <c>Close(ответ)</c> (закрыли без ответа — default).
/// </summary>
public class DialogWindow : Window
{
    private static readonly Uri IconUri = new("avares://eViSTool/Assets/eViSTool.ico");
    private static WindowIcon? _icon;

    /// <summary>Окна, открытые модально прямо сейчас: пока есть хоть одно, новые цепляются к самому верхнему окну.</summary>
    private static readonly List<DialogWindow> OpenModals = [];

    private object? _result;

    public DialogWindow()
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Icon = _icon ??= new WindowIcon(AssetLoader.Open(IconUri));
    }

    /// <summary>
    /// Закрыть окно с ответом для <see cref="ShowModal{TResult}"/>. Своё, а не из Window: Window.Close(object) ответ
    /// отдаёт только тому, кто открыл окно через ShowDialog, а без главного окна показ идёт обычным Show.
    /// </summary>
    public new void Close(object? dialogResult)
    {
        _result = dialogResult;
        base.Close(dialogResult);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowCaption.Dark(this); // заголовок в цвет окна, как у главного
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    /// <summary>
    /// Показать модально и дождаться закрытия: поток окна при этом не блокируется (<see cref="Modal"/>). Результат —
    /// то, что окно передало в <c>Close(ответ)</c>; закрыли Escape или крестиком — default.
    /// </summary>
    public TResult? ShowModal<TResult>()
    {
        if (!Dispatcher.UIThread.CheckAccess()) return Dispatcher.UIThread.Invoke(ShowModal<TResult>);
        var owner = PickOwner();
        OpenModals.Add(this);
        try
        {
            return Modal.Wait<TResult?>(async () => owner is not null ? await ShowDialog<TResult?>(owner) : await ShowWithoutOwner<TResult>());
        }
        finally
        {
            OpenModals.Remove(this);
        }
    }

    /// <summary>Показать как обычное окно (немодальное) и вернуться сразу.</summary>
    public void ShowModeless()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Invoke(ShowModeless);
            return;
        }
        if (PickOwner() is { } owner) Show(owner);
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Show();
        }
    }

    /// <summary>Без главного окна (запуск, закрытие) — обычное окно по центру экрана, как у MessageDialog.</summary>
    private async Task<TResult?> ShowWithoutOwner<TResult>()
    {
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var closed = new TaskCompletionSource();
        Closed += (_, _) => closed.TrySetResult();
        Show();
        await closed.Task;
        return _result is TResult result ? result : default;
    }

    /// <summary>
    /// Владелец нового окна: открыто модальное окно — самое верхнее видимое (вложенные диалоги цепляются друг за друга,
    /// иначе нижний остался бы доступным); нет — активное окно (вопрос из немодального «Найти сервер» должен встать над
    /// ним, а не над главным); иначе главное. null — главного окна ещё или уже нет.
    /// </summary>
    private static Window? PickOwner() =>
        (OpenModals.Count > 0 ? AvaloniaUiPlatform.TopWindow() : null)
        ?? (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.FirstOrDefault(w => w.IsActive && w.IsVisible) : null)
        ?? AvaloniaUiPlatform.MainWindow;
}

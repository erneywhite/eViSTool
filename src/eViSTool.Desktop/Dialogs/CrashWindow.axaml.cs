using Avalonia.Interactivity;
using Avalonia.Threading;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// «Игра вылетела» / «Мир закрылся из-за ошибки» / «Сервер упал» (DataContext — <see cref="CrashWindowViewModel"/>):
/// виновник, почему так решили, подробности и что с модом сделать. Вся логика — в модели, здесь только вид и показ.
/// </summary>
public partial class CrashWindow : DialogWindow
{
    public CrashWindow() => InitializeComponent();

    /// <summary>
    /// Показать окно вылета и вывести его вперёд. Немодальное и без владельца, как в окне на WPF: вылет приходит, пока
    /// игрок в игре, а программа может быть свёрнута, — окно с владельцем свернулось бы вместе с ней. Поверх всех окон
    /// и в панели задач (Topmost, ShowInTaskbar — в разметке), по центру экрана.
    /// </summary>
    public static void Open(CrashWindowViewModel model)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Invoke(() => Open(model));
            return;
        }
        var window = new CrashWindow { DataContext = model };
        window.Show();
        window.Activate();
    }

    // Escape закрывает окно в DialogWindow, как IsCancel у кнопки «Закрыть» в окне на WPF
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}

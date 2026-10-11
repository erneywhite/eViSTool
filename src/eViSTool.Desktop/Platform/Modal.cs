using Avalonia.Threading;

namespace eViSTool.Desktop.Platform;

/// <summary>
/// Дождаться асинхронного окна (вопрос, выбор файла) синхронно — как MessageBox и ShowDialog в WPF. Модели спрашивают
/// и ждут ответа на месте (<c>if (Ui.Confirm(...))</c>), а в Avalonia окна и выбор файлов асинхронные. Поток окна при
/// этом не блокируется: крутится вложенный цикл диспетчера, окно рисуется и отвечает на ввод.
/// </summary>
internal static class Modal
{
    public static T Wait<T>(Func<Task<T>> show)
    {
        var frame = new DispatcherFrame();
        Task<T>? task = null;
        Dispatcher.UIThread.Post(() =>
        {
            task = show();
            task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
        });
        Dispatcher.UIThread.PushFrame(frame);
        return task!.GetAwaiter().GetResult(); // уже завершена; ошибка окна — тому, кто спрашивал
    }
}

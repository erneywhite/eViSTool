using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using eViSTool.Core.Localization;

namespace eViSTool.App;

public partial class App : Application
{
    /// <summary>Сюда пишутся отчёты об ошибках (папка logs в папке данных).</summary>
    public static string LogDir => Core.AppPaths.Logs;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ни одна ошибка не должна ронять окно молча: пишем отчёт и показываем текст
        DispatcherUnhandledException += OnDispatcherError;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => WriteCrashLog(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { WriteCrashLog(a.Exception); a.SetObserved(); };
        Presentation.Ui.Platform = new WpfUiPlatform(); // модели спрашивают, открывают файлы и ставят таймеры через него
        Presentation.ViewModels.AppWindows.Current = new WpfAppWindows(); // модели открывают окна программы через него
        Core.AppPaths.MigrateLegacy(); // до первого чтения настроек
        Core.AppUpdate.AppUpdater.CleanupOld(AppContext.BaseDirectory); // хвосты прошлого обновления
        base.OnStartup(e);
        // снимок для сверки с окном на Avalonia: главное окно к этому моменту уже создано и показано
        if (Presentation.Snapshots.FromEnvironment() is { } snapshot)
            Dispatcher.BeginInvoke(() => _ = SnapshotAsync(snapshot), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Снимок окна (EVISTOOL_SNAPSHOT, см. <see cref="Presentation.Snapshots"/>): экран → PNG → выход.</summary>
    private async Task SnapshotAsync(Presentation.SnapshotRequest request)
    {
        try
        {
            if (MainWindow is not { Content: FrameworkElement content, DataContext: Presentation.ViewModels.MainViewModel main } window) return;
            window.WindowState = WindowState.Normal;
            window.SizeToContent = SizeToContent.WidthAndHeight;
            content.Width = request.Width;   // содержимое — ровно нужного размера (без рамки окна), как ClientSize у Avalonia
            content.Height = request.Height;
            // окно программы (dialog.*) модальное: Navigate не вернётся, пока его не закроют, — поэтому переход отдельно,
            // а снимок — по таймеру (вложенный цикл диспетчера его выполнит)
            _ = Dispatcher.BeginInvoke(() => Presentation.Snapshots.Navigate(main, request));
            await Task.Delay(request.Wait);
            // у окна программы — его корень (рамка шаблона окна): в нём и фон окна, и вся область без заголовка
            var target = Presentation.Snapshots.IsDialog(request)
                         && Windows.OfType<Window>().LastOrDefault(w => w.IsVisible && w != window) is { } dialog
                         && System.Windows.Media.VisualTreeHelper.GetChildrenCount(dialog) > 0
                ? System.Windows.Media.VisualTreeHelper.GetChild(dialog, 0) as FrameworkElement ?? content
                : content;
            target.UpdateLayout();
            var (w, h) = target == content ? (request.Width, request.Height) : ((int)Math.Ceiling(target.ActualWidth), (int)Math.Ceiling(target.ActualHeight));
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            // часть окон программы — с фоном Mica (прозрачным для снимка): подложить лесной фон, как их видно на экране
            var under = new System.Windows.Media.DrawingVisual();
            using (var dc = under.RenderOpen())
                dc.DrawRectangle((System.Windows.Media.Brush)FindResource("Forest.Base"), null, new Rect(0, 0, w, h));
            bitmap.Render(under);
            bitmap.Render(target);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutPath))!);
            using (var file = File.Create(request.OutPath)) png.Save(file);
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
        }
        Shutdown();
    }

    private static string? _restartExe;

    /// <summary>
    /// Закрыть окно обычным путём (с вопросами про несохранённое) и после выхода запустить новую версию.
    /// Если закрытие отменили — перезапуска не будет.
    /// </summary>
    public static void RestartAfterClose(string exe)
    {
        _restartExe = exe;
        Current.MainWindow?.Close();
        if (Current.MainWindow is { IsVisible: true }) _restartExe = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_restartExe is { } exe && File.Exists(exe))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception ex) { WriteCrashLog(ex); }
        }
        base.OnExit(e);
    }

    private void OnDispatcherError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var log = WriteCrashLog(e.Exception);
        MessageBox.Show(
            Loc.T("crash.text", e.Exception.Message, log),
            Loc.T("crash.title"), MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true; // окно продолжает работать
    }

    public static string? WriteCrashLog(Exception? ex)
    {
        if (ex is null) return null;
        try
        {
            Directory.CreateDirectory(LogDir);
            var path = Path.Combine(LogDir, $"error-{DateTime.Now:yyyy-MM-dd}.log");
            File.AppendAllText(path, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====\n{ex}\n\n", Encoding.UTF8);
            return path;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

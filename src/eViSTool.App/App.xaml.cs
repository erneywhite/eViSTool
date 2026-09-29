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
        Core.AppPaths.MigrateLegacy(); // до первого чтения настроек
        base.OnStartup(e);
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

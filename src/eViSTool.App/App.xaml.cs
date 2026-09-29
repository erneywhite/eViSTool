using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace eViSTool.App;

public partial class App : Application
{
    /// <summary>%LOCALAPPDATA%\eViSTool\logs — сюда пишутся отчёты об ошибках.</summary>
    public static string LogDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "eViSTool", "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        // ни одна ошибка не должна ронять окно молча: пишем отчёт и показываем текст
        DispatcherUnhandledException += OnDispatcherError;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => WriteCrashLog(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { WriteCrashLog(a.Exception); a.SetObserved(); };
        base.OnStartup(e);
    }

    private void OnDispatcherError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var log = WriteCrashLog(e.Exception);
        MessageBox.Show(
            $"Что-то пошло не так:\n\n{e.Exception.Message}\n\nПодробности сохранены в:\n{log}",
            "eViSTool — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
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

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using eViSTool.Core.Localization;
using eViSTool.Desktop.Platform;
using eViSTool.Desktop.Views;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop;

public partial class App : Application
{
    private static string? _restartExe;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // ни одна ошибка не должна ронять окно молча: пишем отчёт и показываем текст
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            var log = CrashLog.Write(e.Exception);
            Ui.Error(Loc.T("crash.text", e.Exception.Message, log));
            e.Handled = true; // окно продолжает работать
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => CrashLog.Write(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { CrashLog.Write(a.Exception); a.SetObserved(); };
        Core.AppPaths.MigrateLegacy(); // до первого чтения настроек
        Core.AppUpdate.AppUpdater.CleanupOld(AppContext.BaseDirectory); // хвосты прошлого обновления

        Ui.Platform = new AvaloniaUiPlatform(); // модели спрашивают, открывают файлы и ставят таймеры через него

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) => StartRestart();
            var main = new MainWindow { DataContext = new MainViewModel() };
            desktop.MainWindow = main;
            // снимок для сверки с окном на WPF: экран → PNG → выход (см. Presentation.Snapshots)
            if (Snapshots.FromEnvironment() is { } snapshot)
                main.Opened += (_, _) => Dispatcher.UIThread.Post(() => _ = SnapshotRunner.RunAsync(desktop, snapshot), DispatcherPriority.ApplicationIdle);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Закрыть окно обычным путём (с вопросами про несохранённое) и после выхода запустить новую версию.
    /// Если закрытие отменили — перезапуска не будет.
    /// </summary>
    public static void RestartAfterClose(string exe)
    {
        _restartExe = exe;
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main }) return;
        main.Close();
        if (main.IsVisible) _restartExe = null;
    }

    private static void StartRestart()
    {
        if (_restartExe is not { } exe || !File.Exists(exe)) return;
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) { CrashLog.Write(ex); }
    }
}

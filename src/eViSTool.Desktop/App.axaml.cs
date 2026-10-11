using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using eViSTool.Core.Localization;
using eViSTool.Desktop.Platform;
using eViSTool.Presentation;

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
            // временное окно каркаса: главное окно и экраны переносятся на этапе 2
            desktop.MainWindow = new Window
            {
                Title = "eViSTool",
                Width = 900,
                Height = 560,
                Content = new TextBlock { Text = "eViSTool — каркас окна на Avalonia", Margin = new Thickness(24), FontSize = 22 },
            };
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

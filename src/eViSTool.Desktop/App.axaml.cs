using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
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
            // окно уже закрыто (ошибка при выходе) — показать сообщение не над чем, остаётся отчёт в logs
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow.IsVisible: true })
                Ui.Error(Loc.T("crash.text", e.Exception.Message, log));
            e.Handled = true; // окно продолжает работать
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => CrashLog.Write(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { CrashLog.Write(a.Exception); a.SetObserved(); };
        Core.AppPaths.MigrateLegacy(); // до первого чтения настроек
        Core.AppUpdate.AppUpdater.CleanupOld(AppContext.BaseDirectory); // хвосты прошлого обновления
        PreloadForRestart();

        Ui.Platform = new AvaloniaUiPlatform(); // модели спрашивают, открывают файлы и ставят таймеры через него
        AppWindows.Current = new AvaloniaAppWindows(); // и открывают окна программы
        Hosting.HostedTransports.Register(); // FTP и SFTP для серверов у хостера
        // в системе выключены анимации — переходы мгновенные, проявление сразу, бегущее стоит (docs/design.md, раздел 3).
        // Windows отвечает сразу — стили встают до первого окна; ответ gsettings на Linux приходит из фона, и стили,
        // подключённые тогда, действуют и на уже открытое окно
        SystemMotion.Apply(SystemMotion.ReadAsync(), enabled =>
        {
            if (!enabled)
                Styles.Add(new StyleInclude(new Uri("avares://eViSTool/App.axaml")) { Source = new Uri("avares://eViSTool/Themes/ForestStill.axaml") });
        });

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) => StartRestart();
            var main = new MainWindow { DataContext = new MainViewModel() };
            desktop.MainWindow = main;
            // второй запуск из той же папки — показать это окно, а не открывать новое
            if (Program.Instance is { } instance)
                instance.ActivateRequested += () => Dispatcher.UIThread.Post(() => BringToFront(main));
            // ярлык в меню приложений Linux — после настроек: описание в нём на языке программы
            if (Snapshots.FromEnvironment() is null) LinuxDesktopEntry.Ensure();
            // снимок: экран → PNG → выход (см. Presentation.Snapshots)
            if (Snapshots.FromEnvironment() is { } snapshot)
                main.Opened += (_, _) => Dispatcher.UIThread.Post(() => _ = SnapshotRunner.RunAsync(desktop, snapshot), DispatcherPriority.ApplicationIdle);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Закрыть окно обычным путём (с вопросами про несохранённое) и после выхода запустить новую версию.
    /// Если закрытие отменили — перезапуска не будет.
    /// </summary>
    /// <summary>Показать окно поверх остальных: развернуть из панели задач и сделать активным.</summary>
    internal static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        // поверх чужих окон Windows выводит неохотно — короткое «поверх всех» это обходит
        window.Topmost = true;
        window.Topmost = false;
    }

    public static void RestartAfterClose(string exe)
    {
        _restartExe = exe;
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main }) return;
        main.Close();
        if (main.IsVisible) _restartExe = null;
    }

    /// <summary>
    /// На Linux окно — один файл с .NET внутри, и сборки .NET из него грузятся по мере надобности. Обновление подменяет
    /// этот файл, пока окно ещё работает, и то, что понадобится после подмены, старое окно в новом файле уже не найдёт:
    /// перезапуск падал на «Could not load System.IO.Pipes». Поэтому всё, что нужно для запуска новой версии, — заранее.
    /// </summary>
    private static void PreloadForRestart()
    {
        if (OperatingSystem.IsWindows()) return; // там .NET установлен в системе, а не лежит в exe
        _ = typeof(Process).Assembly;
        _ = typeof(System.IO.Pipes.AnonymousPipeServerStream).Assembly;
        _ = typeof(System.ComponentModel.Win32Exception).Assembly;
        // загрузка типа ещё не грузит то, что нужно его методам, — холостой запуск тем же путём, что и перезапуск
        try { using var p = Process.Start(new ProcessStartInfo("/bin/true") { UseShellExecute = true }); p?.WaitForExit(2000); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private static void StartRestart()
    {
        if (_restartExe is not { } exe || !File.Exists(exe)) return;
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) { CrashLog.Write(ex); }
    }
}

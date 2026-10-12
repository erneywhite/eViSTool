using System.Diagnostics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using eViSTool.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]
// все тесты идут на одном потоке окна, а RemoteImage и жесты держат общее состояние: без параллели надёжнее
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace eViSTool.Desktop.Tests;

/// <summary>Приложение тестов: тема Fluent и таблица, как в окне, отрисовка Skia без экрана.</summary>
public sealed class TestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://eViSTool.Desktop.Tests"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml"),
        });
    }
}

/// <summary>Команда для проверок: запоминает, с чем её выполнили.</summary>
public sealed class RecordingCommand : ICommand
{
    public List<object?> Calls { get; } = [];

    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => Calls.Add(parameter);
}

internal static class Wait
{
    /// <summary>
    /// Ждать условия, давая поработать очереди окна (туда приходят ответы фоновых загрузок). Запас — с расчётом на
    /// медленные машины CI (запись настроек на диск там не укладывалась в 5 с); выполненное условие не ждёт.
    /// </summary>
    public static async Task Until(Func<bool> condition, int timeoutMs = 20000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("условие не выполнилось");
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Просто дать очереди окна и фоновым задачам поработать.</summary>
    public static async Task Idle(int ms = 200)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < ms)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }
}

using System.Diagnostics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using eViSTool.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]
// одно приложение и один поток окна на все тесты: при пересоздании на каждый тест (PerTest) Avalonia под нагрузкой
// иногда трогала общий цикл отрисовки из чужого потока — тест падал на уборке («VerifyAccess»), не на своей проверке
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]
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
        // тесты подключают тему окна (avares://eViSTool/Themes/…) в конструкторах, а xunit v3 идёт в случайном порядке:
        // если сборку окна до того не загрузил ни один тест, Avalonia не находит её скомпилированный XAML
        // («No precompiled XAML found»), и падает добрая половина набора. Загружаем её здесь, до любого теста
        _ = typeof(eViSTool.Desktop.App).Assembly;
        // как App: проявление и «живое» окно — до первого окна, а не от того, какой тест первым тронет Motion
        eViSTool.Desktop.Controls.Motion.Init();
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

    /// <summary>
    /// Дать часам анимаций пройти <paramref name="ms"/> миллисекунд. Окно без экрана двигает их только по тику
    /// отрисовки — тикаем сами, примерно раз в 10 мс; переход или проявление к концу ожидания закончены.
    /// </summary>
    public static async Task Frames(int ms)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        } while (watch.ElapsedMilliseconds < ms);
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

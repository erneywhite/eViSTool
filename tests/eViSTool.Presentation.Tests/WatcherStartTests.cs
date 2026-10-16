using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Таймеры фоновых наблюдателей главного окна: интервал прежний, а повторный Start не подписывает Tick второй раз —
/// иначе каждая проверка шла бы по две за тик. Таймеры подменены ручными: что подписано и запущено — видно сразу,
/// срабатывают они, когда скажет тест.
/// </summary>
public sealed class WatcherStartTests : IDisposable
{
    private readonly IUiPlatform? _oldPlatform = Ui.IsSet ? Ui.Platform : null;
    private readonly List<CountingTimer> _timers = [];

    public WatcherStartTests() => Ui.Platform = NewProfileViewModelTests.Fake<IUiPlatform>.Of(("CreateTimer", args =>
    {
        var timer = new CountingTimer { Interval = (TimeSpan)args[0]! };
        _timers.Add(timer);
        return timer;
    }));

    public void Dispose() => Ui.Platform = _oldPlatform!; // как было; null — не задано (следующий тест поставит своё)

    private CountingTimer OnlyTimer => Assert.Single(_timers);

    [Fact]
    public void GameWatcher_StartTwice_ChecksOncePerTick()
    {
        var checks = 0;
        var watcher = new GameWatcher(() => null, (_, _) => checks++, (_, _) => { }, _ => { }, () => { }, (_, _) => { });

        watcher.Start();
        watcher.Start();
        OnlyTimer.Fire(); // активного клиентского профиля нет — проверка проходит сразу, без фоновых задач

        Assert.Equal(TimeSpan.FromSeconds(2), OnlyTimer.Interval);
        Assert.True(OnlyTimer.IsEnabled);
        Assert.Equal(1, OnlyTimer.Subscribers);
        Assert.Equal(1, checks);
    }

    [Fact]
    public void ServerCrashWatcher_StartTwice_SubscribesOnce()
    {
        var watcher = new ServerCrashWatcher([], (_, _) => { }, (_, _) => { });

        watcher.Start();
        watcher.Start();

        Assert.Equal(TimeSpan.FromSeconds(15), OnlyTimer.Interval);
        Assert.True(OnlyTimer.IsEnabled);
        Assert.Equal(1, OnlyTimer.Subscribers);
    }

    [Fact]
    public void RemoteAgentKeeper_StartTwice_SubscribesOnce()
    {
        var keeper = new RemoteAgentKeeper([]);

        keeper.Start();
        keeper.Start();

        Assert.Equal(TimeSpan.FromSeconds(30), OnlyTimer.Interval);
        Assert.True(OnlyTimer.IsEnabled);
        Assert.Equal(1, OnlyTimer.Subscribers);
    }

    [Fact]
    public void DiskUpdateWatcher_StartTwice_ChecksOncePerTick()
    {
        var checks = 0;
        // «обновило само окно» — проверка на этом и заканчивается, exe на диске не трогается
        var watcher = new DiskUpdateWatcher(() => { checks++; return true; }, _ => { });

        watcher.Start();
        watcher.Start();
        OnlyTimer.Fire();

        Assert.Equal(TimeSpan.FromSeconds(10), OnlyTimer.Interval);
        Assert.True(OnlyTimer.IsEnabled);
        Assert.Equal(1, OnlyTimer.Subscribers);
        Assert.Equal(1, checks);
    }

    /// <summary>Ручной таймер: считает подписчиков Tick и срабатывает, когда скажет тест.</summary>
    private sealed class CountingTimer : IUiTimer
    {
        private EventHandler? _tick;

        public TimeSpan Interval { get; set; }
        public bool IsEnabled { get; private set; }
        public int Subscribers { get; private set; }

        public event EventHandler? Tick
        {
            add { _tick += value; Subscribers++; }
            remove { _tick -= value; Subscribers--; }
        }

        public void Start() => IsEnabled = true;
        public void Stop() => IsEnabled = false;

        /// <summary>Прошёл интервал: таймер сработал (если запущен).</summary>
        public void Fire()
        {
            if (IsEnabled) _tick?.Invoke(this, EventArgs.Empty);
        }
    }
}

using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Наблюдатель за игрой без запущенной игры: проверка сообщает «не запущена», наблюдения нет, отложенное до выхода из
/// игры ждёт. Сами процессы игры в тестах не запускаются — активного клиентского профиля нет, процессы не ищутся.
/// </summary>
public sealed class GameWatcherTests
{
    private sealed class Probe
    {
        public List<(GameProfile? Profile, bool Running)> Checked { get; } = [];
        public List<string> Events { get; } = [];

        public GameWatcher Watcher(Func<GameProfile?>? active = null) => new(
            active ?? (() => null),
            (p, running) => Checked.Add((p, running)),
            (p, _) => Events.Add("errors:" + p.Id),
            p => Events.Add("pending:" + p.Id),
            () => Events.Add("exited"),
            (p, _) => Events.Add("crash:" + p.Id));
    }

    // активный профиль не клиентский (или его нет) — проверили «ничего», игра не запущена, наблюдать не за чем
    [Fact]
    public void Check_WithoutActiveClient_ReportsNotRunning_AndWatchesNothing() => Sta.Run(() =>
    {
        var probe = new Probe();
        var watcher = probe.Watcher();

        Sta.Wait(watcher.CheckAsync());

        Assert.Equal(new (GameProfile?, bool)[] { (null, false) }, probe.Checked);
        Assert.Empty(probe.Events);
        Assert.False(watcher.IsWatching);
    });

    // дело «после выхода из игры» ждёт её закрытия: без наблюдаемой игры проверка его не выполняет
    [Fact]
    public void RunAfterExit_WaitsForTheGameToClose() => Sta.Run(() =>
    {
        var probe = new Probe();
        var watcher = probe.Watcher();
        var profile = new GameProfile { Id = "gw-client", Name = "Игра", Kind = ProfileKind.Client };
        var ran = 0;

        watcher.RunAfterExit(profile, () => { ran++; return Task.CompletedTask; });
        Sta.Wait(watcher.CheckAsync());

        Assert.Equal(0, ran);
        Assert.Empty(probe.Events);
        Assert.False(watcher.IsRunningFor(profile));
    });
}

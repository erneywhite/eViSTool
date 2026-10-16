using eViSTool.Core.Diagnostics;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Наблюдатель за падениями серверов: о каком падении из статуса агента сказать (новое — да, то же — нет, случившееся
/// до открытия окна — нет), и что без агента опрос молчит. Агентов в тестах нет: решение проверяется напрямую, а опрос —
/// на профилях, у которых агента нет и быть не может.
/// </summary>
public sealed class ServerCrashWatcherTests
{
    private static readonly DateTime Opened = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Local);

    private static ServerCrashInfo Crash(string id, DateTime at) => new() { Id = id, At = at, Reason = "test" };

    private static ServerCrashWatcher Watcher(IEnumerable<ProfileViewModel>? profiles = null,
        List<string>? errors = null, List<string>? crashes = null) =>
        new(profiles ?? [], (p, _) => errors?.Add(p.Id), (p, c) => crashes?.Add(p.Id + ":" + c.Id), Opened);

    // сервер упал ещё до того, как открыли окно, — задним числом не тревожим (и потом про это же падение — тоже)
    [Fact]
    public void FirstPoll_CrashBeforeTheWindowOpened_IsNotShown() => Sta.Run(() =>
    {
        var watcher = Watcher();

        Assert.False(watcher.Notice("srv", Crash("a", Opened.AddHours(-1))));
        Assert.False(watcher.Notice("srv", Crash("a", Opened.AddHours(-1))));
    });

    [Fact]
    public void FirstPoll_CrashAfterTheWindowOpened_IsShownOnce() => Sta.Run(() =>
    {
        var watcher = Watcher();

        Assert.True(watcher.Notice("srv", Crash("a", Opened.AddMinutes(1))));
        Assert.False(watcher.Notice("srv", Crash("a", Opened.AddMinutes(1)))); // тот же Id — уже сказали
    });

    // задним числом не тревожим только при первом опросе: следующее падение — новое, о нём говорим
    [Fact]
    public void NextCrash_AfterTheFirstPoll_IsShown() => Sta.Run(() =>
    {
        var watcher = Watcher();

        Assert.False(watcher.Notice("srv", Crash("old", Opened.AddHours(-2))));
        Assert.True(watcher.Notice("srv", Crash("new", Opened.AddHours(-1))));
    });

    // первый опрос без падения уже «первый»: появившееся потом падение показывается, когда бы оно ни случилось
    [Fact]
    public void FirstPollWithoutCrash_ThenCrash_IsShown() => Sta.Run(() =>
    {
        var watcher = Watcher();

        Assert.False(watcher.Notice("srv", null));
        Assert.True(watcher.Notice("srv", Crash("a", Opened.AddHours(-1))));
    });

    [Fact]
    public void Profiles_AreTrackedSeparately() => Sta.Run(() =>
    {
        var watcher = Watcher();

        Assert.True(watcher.Notice("one", Crash("a", Opened.AddMinutes(1))));
        Assert.True(watcher.Notice("two", Crash("a", Opened.AddMinutes(1)))); // у другого сервера своя история
        Assert.False(watcher.Notice("one", Crash("a", Opened.AddMinutes(1))));
    });

    // агента нет (свой сервер не запущен; код удалённого не прочитать) — ни ошибок, ни окон; клиентские не опрашиваются
    [Fact]
    public void Check_ServersWithoutAgents_ReportNothing() => Sta.Run(() =>
    {
        var profiles = new[]
        {
            new ProfileViewModel(new GameProfile { Id = "crashwatch-" + Guid.NewGuid().ToString("N"), Name = "Свой", Kind = ProfileKind.Server }, _ => { }),
            new ProfileViewModel(new GameProfile { Id = "crashwatch-remote", Name = "Чужой", Kind = ProfileKind.Server, RemoteCode = "не-код" }, _ => { }),
            new ProfileViewModel(new GameProfile { Id = "crashwatch-client", Name = "Игра", Kind = ProfileKind.Client }, _ => { }),
        };
        List<string> errors = [], crashes = [];
        var watcher = Watcher(profiles, errors, crashes);

        Sta.Wait(watcher.CheckAsync());

        Assert.Empty(errors);
        Assert.Empty(crashes);
    });
}

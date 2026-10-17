using eViSTool.Core.Hosting;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Notifications;

namespace eViSTool.Core.Tests;

/// <summary>
/// Оповещения сервера у хостера, которые шлёт окно: что оно помнит между запусками, замок «следит одно окно», список
/// вышедших обновлений модов в том же виде, что у агента, и отправка, которую можно дождаться и перехватить.
/// </summary>
public sealed class HostedNotifyTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-hnotify-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---------- состояние между запусками окна

    [Fact]
    public void State_SurvivesARestart_AndABrokenFileIsAFreshStart()
    {
        Assert.Null(HostedNotifyState.Load("p1", _dir).ModsCheckedAt); // файла нет — как первая проверка

        var at = new DateTime(2026, 10, 18, 9, 30, 0, DateTimeKind.Utc);
        new HostedNotifyState { ModsCheckedAt = at, ModUpdates = ["Alpha: 1.0.0 → 1.1.0"] }.Save("p1", _dir);
        var loaded = HostedNotifyState.Load("p1", _dir);

        Assert.Equal(at, loaded.ModsCheckedAt);
        Assert.Equal(["Alpha: 1.0.0 → 1.1.0"], loaded.ModUpdates);
        Assert.Equal(Path.Combine(_dir, "p1.hosted-notify.json"), HostedNotifyState.FileFor("p1", _dir));

        File.WriteAllText(HostedNotifyState.FileFor("p1", _dir), "{ not json");
        Assert.Null(HostedNotifyState.Load("p1", _dir).ModsCheckedAt);
    }

    // ---------- одно окно на профиль

    [Fact]
    public void Lease_IsHeldByOneWindowAtATime_AndFreedOnClose()
    {
        using (var first = HostedWatchLease.TryTake("p1", _dir))
        {
            Assert.NotNull(first);
            Assert.Null(HostedWatchLease.TryTake("p1", _dir));   // второе окно этой машины — не следит
            using var other = HostedWatchLease.TryTake("p2", _dir); // другой профиль — свой замок
            Assert.NotNull(other);
        }
        using var again = HostedWatchLease.TryTake("p1", _dir);   // окно закрылось — следит другое
        Assert.NotNull(again);
    }

    // ---------- вышедшие обновления модов

    private static ModCheckResult Result(string modId, string name, string installed, ModStatus status, string? latest, bool file = true) => new()
    {
        Local = new LocalMod($"/Mods/{modId}.zip", new ModInfo { ModId = modId, OriginalModId = modId, Name = name, Version = installed }, null),
        Status = status,
        LatestCompatible = latest is null ? null : new ModDbRelease { ModVersion = latest, MainFile = file ? $"https://example.test/{modId}.zip" : null },
    };

    [Fact]
    public void Due_IsWhatCanBeInstalled_ByName_InTheAgentsWording()
    {
        var due = ModUpdatesDue.Of(
        [
            Result("zeta", "Zeta", "1.0.0", ModStatus.UpdateAvailable, "1.1.0"),
            Result("alpha", "", "2.0.0", ModStatus.UpdateAvailable, "2.1.0"),    // без названия — по modid
            Result("pinned", "Pinned", "1.0.0", ModStatus.Pinned, "2.0.0"),      // закреплён
            Result("nofile", "No file", "1.0.0", ModStatus.UpdateAvailable, "1.2.0", file: false), // ставить нечего
            Result("fresh", "Fresh", "3.0.0", ModStatus.UpToDate, "3.0.0"),
        ]);

        Assert.Equal(["alpha: 2.0.0 → 2.1.0", "Zeta: 1.0.0 → 1.1.0"], due.Select(ModUpdatesDue.Line));
    }

    // ---------- отправка, которую ждёт окно

    [Fact]
    public async Task NotifyAsync_GoesToTheEventsChannels_InOrder_AndAFailureIsReportedNotThrown()
    {
        var settings = new ServerNotifySettings
        {
            ServerName = "G-Portal",
            Channels = [new NotifyChannel { Id = "a", Name = "Telegram" }, new NotifyChannel { Id = "b", Name = "Discord" }],
            Routes = new() { [NotifyEvent.ServerStarted] = ["a", "b"], [NotifyEvent.PlayerJoined] = ["b"] },
        };
        var sent = new List<(string Channel, NotifyMessage Message)>();
        var errors = new List<string>();
        var notifier = new ServerNotifier(() => settings, new HttpClient(), (channel, error) => errors.Add($"{channel}: {error}"), (channel, message) =>
        {
            if (channel.Id == "b" && message.Title == "boom") throw new NotifyException("refused");
            sent.Add((channel.Id, message));
            return Task.CompletedTask;
        });

        await notifier.NotifyAsync(NotifyEvent.ServerStarted, "Server started");
        await notifier.NotifyAsync(NotifyEvent.ServerStopped, "Server stopped"); // ни в один канал не включено
        await notifier.NotifyAsync(NotifyEvent.PlayerJoined, "boom");

        Assert.Equal(["a", "b"], sent.Select(s => s.Channel));
        Assert.All(sent, s => Assert.Equal("G-Portal", s.Message.Server));
        Assert.Equal(NotifySeverity.Good, sent[0].Message.Severity);
        Assert.Equal(["Discord: refused"], errors);
    }
}

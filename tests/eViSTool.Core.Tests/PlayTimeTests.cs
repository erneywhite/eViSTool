using System.Globalization;
using eViSTool.Core.Game;

namespace eViSTool.Core.Tests;

/// <summary>«Время в игре»: сессии из client-main.log, учёт без повторов, архив логов читается один раз.</summary>
public sealed class PlayTimeTests : IDisposable
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-play-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _profileId = "test-" + Guid.NewGuid().ToString("N")[..8];

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        if (File.Exists(PlayLedger.FileFor(_profileId))) File.Delete(PlayLedger.FileFor(_profileId));
    }

    private static DateTime Utc(string local) => DateTime.Parse(local, Ru, DateTimeStyles.AssumeLocal).ToUniversalTime();

    // как в настоящем логе: две сессии на сервере, неудачное подключение (в мир не вошли) не считается
    private static readonly string[] Log =
    [
        "26.10.2026 22:39:20 [Notification] Client logger started.",
        "26.10.2026 22:39:23 [Notification] Connecting to 192.168.31.31...",
        "26.10.2026 22:40:11 [Notification] Received level finalize",
        "26.10.2026 22:55:04 [Notification] Destroying game session, waiting up to 200ms for client threads to exit",
        "26.10.2026 22:55:05 [Notification] Exiting current game to main menu, reason: leave world button pressed",
        "26.10.2026 22:56:00 [Notification] Connecting to oldwood.example.org:42420...",
        "26.10.2026 22:56:02 [Notification] Destroying game session, waiting up to 200ms for client threads to exit",
        "26.10.2026 22:56:32 [Notification] Connecting to 192.168.31.31:42420...",
        "26.10.2026 22:57:32 [Notification] Received level finalize",
        "26.10.2026 23:18:32 [Notification] Destroying game session, waiting up to 200ms for client threads to exit",
        "26.10.2026 23:18:35 [Notification] Exiting game now. Server running=False. Exit reason: Main screen quit button was pressed",
    ];

    [Fact]
    public void Sessions_FromWorldEntryToLeaving_ByServer()
    {
        var s = PlaySessions.FromLog(Log, Ru);
        Assert.Equal(2, s.Count);
        Assert.Equal((Utc("26.10.2026 22:40:11"), Utc("26.10.2026 22:55:04"), "192.168.31.31"), (s[0].StartUtc, s[0].EndUtc, s[0].Server));
        Assert.Equal("192.168.31.31", s[1].Server); // стандартный порт — тот же сервер
        Assert.Equal(TimeSpan.FromMinutes(21), s[1].Length);
    }

    // без «Connecting to» — одиночная игра; лог оборвался в мире (игра идёт или вылетела) — до последней записи
    [Fact]
    public void Singleplayer_AndOpenSession()
    {
        var s = PlaySessions.FromLog(
        [
            "27.10.2026 10:00:00 [Notification] Client logger started.",
            "27.10.2026 10:01:00 [Notification] Received level finalize",
            "27.10.2026 10:31:00 [Notification] Saving game",
        ], Ru);
        var only = Assert.Single(s);
        Assert.Null(only.Server);
        Assert.Equal(TimeSpan.FromMinutes(30), only.Length);
    }

    // мир одиночной игры — из server-main.log того же запуска (как в настоящем логе)
    [Fact]
    public void Singleplayer_GetsWorldName_FromServerLog()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_root, "Logs")).FullName;
        File.WriteAllLines(Path.Combine(logs, "client-main.log"),
        [
            "29.10.2026 16:37:57 [Notification] Server main instantiated",
            "29.10.2026 16:39:41 [Notification] Received level finalize",
            "29.10.2026 16:39:50 [Notification] Destroying game session, waiting up to 200ms for client threads to exit",
        ]);
        File.WriteAllLines(Path.Combine(logs, "server-main.log"),
        [
            "29.10.2026 16:37:57 [Notification] Loading savegame",
            @"29.10.2026 16:37:57 [Notification] Savegame %appdata%\VintagestoryData\Saves\erneys hermit story.vcdbs loaded",
        ]);
        var ledger = new PlayLedger();
        ledger.Scan(_root, Ru);
        Assert.Equal(PlaySessions.WorldPrefix + "erneys hermit story", Assert.Single(ledger.All).Server);
        Assert.Equal("world", Assert.Single(PlaySessions.Worlds(["1.1.2026 10:00:00 [Notification] Savegame /home/u/.config/VintagestoryData/Saves/world.vcdbs loaded"], Ru)).Name);
    }

    [Fact]
    public void Ledger_MergesWithoutRepeats_ExtendsOpenSession_ReadsArchiveOnce()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_root, "Logs")).FullName;
        var archived = Directory.CreateDirectory(Path.Combine(logs, "Archive", "2026-10-26_22_39_20")).FullName;
        File.WriteAllLines(Path.Combine(archived, "client-main.log"), Log);
        var current = Path.Combine(logs, "client-main.log");
        File.WriteAllLines(current, ["28.10.2026 20:00:00 [Notification] Connecting to 192.168.31.31...", "28.10.2026 20:01:00 [Notification] Received level finalize",
            "28.10.2026 20:11:00 [Notification] tick"]);

        var ledger = new PlayLedger();
        Assert.True(ledger.Scan(_root, Ru));
        Assert.Equal(3, ledger.All.Count);
        Assert.Equal(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(53) + TimeSpan.FromMinutes(21) + TimeSpan.FromMinutes(10), ledger.Total);

        // игра идёт дальше: та же сессия удлиняется, а не добавляется второй; архив второй раз не читается
        File.AppendAllLines(current, ["28.10.2026 20:41:00 [Notification] Destroying game session"]);
        File.Delete(Path.Combine(archived, "client-main.log"));
        Assert.True(ledger.Scan(_root, Ru));
        Assert.Equal(3, ledger.All.Count);
        Assert.Equal(TimeSpan.FromMinutes(40), ledger.Last!.Length);
        Assert.False(ledger.Scan(_root, Ru)); // ничего нового

        var byServer = ledger.ByServer();
        Assert.Equal("192.168.31.31", Assert.Single(byServer).Server);
        Assert.Equal(Utc("26.10.2026 22:40:11"), ledger.SinceUtc);

        ledger.Save(_profileId);
        var back = PlayLedger.Load(_profileId);
        Assert.Equal(ledger.Total, back.Total);
        Assert.False(back.Scan(_root, Ru)); // прочитанный архив запомнен и после перезапуска
        Assert.Empty(PlayLedger.Load("нет-такого-профиля").All);
    }

    // испорченный учёт не затирается: откладывается рядом, а новый начинается с чистого
    [Fact]
    public void BrokenLedger_IsPutAside_NotOverwritten()
    {
        var path = PlayLedger.FileFor(_profileId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"Sessions\": [ { \"StartUtc\": ");
        try
        {
            Assert.Empty(PlayLedger.Load(_profileId).All);
            Assert.False(File.Exists(path));
            var aside = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.broken"));
            Assert.StartsWith("{ \"Sessions\"", File.ReadAllText(aside));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*")) File.Delete(f);
        }
    }
}

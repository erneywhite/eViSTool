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
        var s = PlaySessions.FromLog(Log);
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
        ]);
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
        ledger.Scan(_root);
        Assert.Equal(PlaySessions.WorldPrefix + "erneys hermit story", Assert.Single(ledger.All).Server);
        Assert.Equal("world", Assert.Single(PlaySessions.Worlds(["1.1.2026 10:00:00 [Notification] Savegame /home/u/.config/VintagestoryData/Saves/world.vcdbs loaded"])).Name);
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
        Assert.True(ledger.Scan(_root));
        Assert.Equal(3, ledger.All.Count);
        Assert.Equal(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(53) + TimeSpan.FromMinutes(21) + TimeSpan.FromMinutes(10), ledger.Total);

        // игра идёт дальше: та же сессия удлиняется, а не добавляется второй; архив второй раз не читается
        File.AppendAllLines(current, ["28.10.2026 20:41:00 [Notification] Destroying game session"]);
        File.Delete(Path.Combine(archived, "client-main.log"));
        Assert.True(ledger.Scan(_root));
        Assert.Equal(3, ledger.All.Count);
        Assert.Equal(TimeSpan.FromMinutes(40), ledger.Last!.Length);
        Assert.False(ledger.Scan(_root)); // ничего нового

        var byServer = ledger.ByServer();
        Assert.Equal("192.168.31.31", Assert.Single(byServer).Server);
        Assert.Equal(Utc("26.10.2026 22:40:11"), ledger.SinceUtc);

        ledger.Save(_profileId);
        var back = PlayLedger.Load(_profileId)!;
        Assert.Equal(ledger.Total, back.Total);
        Assert.False(back.Scan(_root)); // прочитанный архив запомнен и после перезапуска
        Assert.Empty(PlayLedger.Load("нет-такого-профиля")!.All);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(PlayLedger.FileFor(_profileId))!, Path.GetFileName(PlayLedger.FileFor(_profileId)) + ".*.tmp"));
    }

    // учёт есть, но сейчас не читается (его держит другой процесс) — не «пустой»: Load говорит null, и поверх не пишут.
    // Раньше такой учёт считался пустым, и следующее сохранение затирало всё накопленное время
    [WindowsFact]
    public void LockedLedger_IsNotTreatedAsEmpty()
    {
        // монопольное открытие файла есть только на Windows (пропуск — см. аудит, п. 6.12)
        var ledger = new PlayLedger();
        ledger.Merge([new PlaySession(Utc("26.10.2026 22:40:11"), Utc("26.10.2026 23:40:11"), null)]);
        ledger.Save(_profileId);
        using (new FileStream(PlayLedger.FileFor(_profileId), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Null(PlayLedger.Load(_profileId));
        Assert.Equal(TimeSpan.FromHours(1), PlayLedger.Load(_profileId)!.Total);
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
            Assert.Empty(PlayLedger.Load(_profileId)!.All);
            Assert.False(File.Exists(path));
            var aside = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.broken"));
            Assert.StartsWith("{ \"Sessions\"", File.ReadAllText(aside));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*")) File.Delete(f);
        }
    }

    [Fact]
    public void Session_ThroughTheRepeatedAutumnHour_KeepsItsRealLength()
    {
        // аудит 4.3: Рига, 2026-10-25 в 04:00 часы назад на 03:00. Вход в 03:40 (летнее), выход в 03:10 (уже зимнее):
        // на самом деле 30 минут, а не минус полчаса
        var riga = TimeZoneInfo.FindSystemTimeZoneById("Europe/Riga");
        var s = PlaySessions.FromLog(
        [
            "25.10.2026 03:40:00 [Notification] Received level finalize",
            "25.10.2026 03:55:00 [Notification] Saving",
            "25.10.2026 03:10:00 [Notification] Destroying game session, waiting up to 200ms for client threads to exit",
        ], riga);
        Assert.Single(s);
        Assert.Equal(new DateTime(2026, 10, 25, 0, 40, 0, DateTimeKind.Utc), s[0].StartUtc);
        Assert.Equal(TimeSpan.FromMinutes(30), s[0].Length);
    }

    // ModDB, 2026-11-11: у американского игрока время в игре вышло в 100 раз больше. Игра пишет дату своим форматом
    // «d.M.yyyy» при любом языке системы, а разбор по формату системы читал «8.11» как 11 августа, а «30.10» не читал
    [Fact]
    public void GameDates_ReadTheSameInAnySystemLanguage()
    {
        var was = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            var s = PlaySessions.FromLog(
            [
                "8.11.2026 21:23:45 [Notification] Received level finalize",
                "8.11.2026 22:23:45 [Notification] Destroying game session, waiting up to 200ms for client threads to exit",
                "30.10.2026 10:00:00 [Notification] Received level finalize",
                "30.10.2026 10:30:00 [Notification] Destroying game session, waiting up to 200ms for client threads to exit",
            ]);
            Assert.Equal(2, s.Count);
            Assert.Equal(TimeSpan.FromHours(1), s[0].Length);
            Assert.Equal(TimeSpan.FromMinutes(30), s[1].Length);
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
        Assert.Equal(new DateTime(2026, 11, 8, 21, 23, 45), eViSTool.Core.Diagnostics.GameLog.ParseTime("8.11.2026 21:23:45"));
        Assert.Equal(new DateTime(2026, 9, 30, 7, 5, 0), eViSTool.Core.Diagnostics.GameLog.ParseTime("30.9.2026 07:05:00"));    }

    [Theory]
    [InlineData("ru-RU", true)]
    [InlineData("de-DE", true)]
    [InlineData("en-US", false)]
    [InlineData("en-GB", true)]
    public void ReadsGameDates_OnlyWhereTheOldParsingWasRight(string culture, bool right) =>
        Assert.Equal(right, PlayLedger.ReadsGameDates(CultureInfo.GetCultureInfo(culture)));

    // учёт до 1.1.1 (поля DateFormat нет): у американского игрока он неверный — собирается заново, у русского остаётся
    [Theory]
    [InlineData("en-US", 0)]
    [InlineData("ru-RU", 1)]
    public void OldLedger_IsRebuilt_OnlyWhereItWasMisread(string culture, int kept)
    {
        var path = PlayLedger.FileFor(_profileId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            { "Sessions": [ { "StartUtc": "2026-08-11T18:23:45Z", "EndUtc": "2026-10-10T07:00:00Z", "Server": null } ],
              "ScannedArchives": [ "2026-10-26_22_39_20" ] }
            """);
        var was = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var ledger = PlayLedger.Load(_profileId)!;
            Assert.Equal(kept, ledger.All.Count);
            ledger.Save(_profileId);
            // сохранённый новой версией учёт уже верный — второй раз не сбрасывается
            ledger.Merge([new PlaySession(Utc("8.11.2026 21:00:00"), Utc("8.11.2026 22:00:00"), null)]);
            ledger.Save(_profileId);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal(kept + 1, PlayLedger.Load(_profileId)!.All.Count);
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }
}

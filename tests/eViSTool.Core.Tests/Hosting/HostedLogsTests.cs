using System.Text;
using eViSTool.Core.Diagnostics;
using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// Лог сервера у хостера: опрос текущего запуска, перезапуски, состояние, игроки, архив и вылеты — на поддельном FTP
/// «как G-Portal» с настоящими логами. Время файлов ставится как у G-Portal: MLSD отдаёт UTC, часы сервера — UTC+2.
/// </summary>
public sealed class HostedLogsTests : IDisposable
{
    private const string Main = "/Logs/server-main.log";
    private readonly HostedFixture _host = new();
    private readonly FakeTime _time = new(new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc));

    public HostedLogsTests() => _host.GPortalLayout(log: false);

    public void Dispose() => _host.Dispose();

    private HostedLogs Logs() => new(_host.Target) { Time = _time };

    /// <summary>Время файла, как его отдаст MLSD G-Portal: последняя строка только что записана, часы сервера — UTC+2.</summary>
    private static DateTime GPortalTime(IEnumerable<string> lines)
    {
        var marks = new HostedRunMarks();
        foreach (var line in lines) marks.Add(line);
        return DateTime.SpecifyKind(marks.LastTime!.Value.AddHours(-2), DateTimeKind.Utc);
    }

    /// <summary>Положить лог на «сервер» (время файла — как у G-Portal), вернуть путь на этой машине.</summary>
    private string PutLog(string remote, string[] lines, DateTime? modifiedUtc = null)
    {
        var local = _host.Server.Put(remote, HostedLogSamples.Bytes(lines));
        File.SetLastWriteTimeUtc(local, modifiedUtc ?? (lines.Length > 0 ? GPortalTime(lines) : _time.UtcNow));
        return local;
    }

    private static void Append(string local, byte[] bytes)
    {
        using var file = new FileStream(local, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        file.Write(bytes);
    }

    private static void Append(string local, IEnumerable<string> lines) => Append(local, HostedLogSamples.Bytes(lines));

    /// <summary>Тот же лог на следующий день — новый запуск.</summary>
    private static string[] NextDay(IEnumerable<string> lines) => [.. lines.Select(l => l.Replace("17.10.2026 ", "18.10.2026 "))];

    /// <summary>Какие файлы читались (скачивались или кусками).</summary>
    private List<string> TrackReads()
    {
        var touched = new List<string>();
        _host.Server.OnBefore = (op, path) => { if (op is "download" or "read") lock (touched) touched.Add(path); };
        return touched;
    }

    private static string[] Between(string[] lines, int from, string until) =>
        lines[from..(Array.FindIndex(lines, from, l => l.Contains(until, StringComparison.Ordinal)) + 1)];

    /// <summary>Поток предупреждений в 13:35 — с ним лог вырастает больше <see cref="HostedLogs.FirstReadLimit"/>.</summary>
    private static IEnumerable<string> Overloaded(int count = 9000) =>
        Enumerable.Range(0, count).Select(i => $"17.10.2026 13:35:{i % 60:00} [Warning] Server overloaded. A tick took {500 + i % 100}ms to complete.");

    /// <summary>UTC для времени этой машины: подделка с ListLocalTime отдаёт его в листинге, как LIST.</summary>
    private static DateTime LocalClock(int hour, int minute, int second) =>
        new DateTime(2026, 10, 17, hour, minute, second, DateTimeKind.Local).ToUniversalTime();

    // ---------- текущий запуск

    [Fact]
    public async Task FirstPoll_ReadsTheWholeLog_RunningServerWithAPlayer()
    {
        PutLog(Main, HostedLogSamples.Running);
        var logs = Logs();

        var update = await logs.PollAsync(default);

        Assert.Equal(HostedServerState.Running, update.State);
        Assert.False(update.Restarted);
        Assert.Equal(HostedLogSamples.Running, update.NewLines);
        Assert.Equal(HostedLogSamples.Running, logs.Lines);
        var player = Assert.Single(update.Players);
        Assert.Equal("Erney", player.Name);
        Assert.Equal(new DateTime(2026, 10, 17, 13, 31, 18), player.Since); // время сервера из строки, не UTC файла
        Assert.Equal("203.0.113.7:56289", player.Address);
        Assert.Equal("2026-10-17_13_27_08", logs.RunName);
        Assert.Equal(new DateTime(2026, 10, 17, 13, 27, 8), logs.RunStarted);
        Assert.Equal(TimeSpan.FromHours(2), logs.ServerUtcOffset);
        Assert.Equal("/Logs", logs.LogsDir); // папка данных найдена по serverconfig.json в корне
    }

    [Fact]
    public async Task Tail_ArrivesInParts_UnfinishedLineWaits_EvenInTheMiddleOfALetter()
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        var logs = Logs();
        await logs.PollAsync(default);
        var line = "17.10.2026 13:31:20 [Notification] Привет, мир";
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        var cut = Encoding.UTF8.GetByteCount("17.10.2026 13:31:20 [Notification] П") - 1; // посреди «П»

        Append(local, bytes[..cut]);
        Assert.Empty((await logs.PollAsync(default)).NewLines);
        Assert.Empty((await logs.PollAsync(default)).NewLines); // и без роста — ничего

        Append(local, bytes[cut..]);
        var update = await logs.PollAsync(default);

        Assert.Equal([line], update.NewLines);
        Assert.Equal([.. HostedLogSamples.Running, line], logs.Lines);
        Assert.False(update.Restarted);
    }

    [Fact]
    public async Task LinesReadByChecks_StillComeWithTheNextPoll()
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        var logs = Logs();
        await logs.PollAsync(default);

        Append(local, ["17.10.2026 13:31:20 [Notification] one"]);
        await logs.CheckNowAsync(default);
        Append(local, ["17.10.2026 13:31:21 [Notification] two"]);
        await logs.FindCrashAsync(default);

        Assert.Equal(["17.10.2026 13:31:20 [Notification] one", "17.10.2026 13:31:21 [Notification] two"], (await logs.PollAsync(default)).NewLines);

        // перезапуск, замеченный проверкой, окно тоже узнает при своём опросе
        var fresh = NextDay(HostedLogSamples.Starting);
        PutLog(Main, fresh);
        await logs.CheckNowAsync(default);
        var update = await logs.PollAsync(default);

        Assert.True(update.Restarted);
        Assert.Equal(fresh, update.NewLines);
        Assert.False((await logs.PollAsync(default)).Restarted);
    }

    [Fact]
    public async Task PlayerLeavesAndComesBack()
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        var logs = Logs();
        await logs.PollAsync(default);
        var changes = 0;
        logs.Players.Changed += () => Interlocked.Increment(ref changes);
        var stopped = HostedLogSamples.Stopped;

        Append(local, Between(stopped, HostedLogSamples.Running.Length, "13:31:29 [Event] Client 3 disconnected."));
        Assert.Empty((await logs.PollAsync(default)).Players);

        Append(local, Between(stopped, Array.FindIndex(stopped, l => l.Contains("13:31:29 [Event] Client 3 disconnected.")) + 1,
            "13:31:42 [Event] Erney"));
        var back = Assert.Single((await logs.PollAsync(default)).Players);

        Assert.Equal(new DateTime(2026, 10, 17, 13, 31, 42), back.Since);
        Assert.Equal("203.0.113.7:56356", back.Address);
        Assert.Equal(2, changes); // вышел, вошёл
    }

    [Fact]
    public async Task StopFromThePanel_IsStopped_AndNobodyIsOnline()
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        var logs = Logs();
        await logs.PollAsync(default);

        Append(local, HostedLogSamples.Stopped[HostedLogSamples.Running.Length..]);
        var update = await logs.PollAsync(default);

        Assert.Equal(HostedServerState.Stopped, update.State);
        Assert.True(HostedLogs.IsSurelyDown(update.State));
        Assert.Empty(update.Players);
        Assert.Equal(HostedLogSamples.Stopped, logs.Lines);
    }

    [Fact]
    public async Task StartingServer_IsStarting()
    {
        PutLog(Main, HostedLogSamples.Starting);

        var update = await Logs().PollAsync(default);

        Assert.Equal(HostedServerState.Starting, update.State);
        Assert.False(HostedLogs.IsSurelyDown(update.State));
    }

    [Fact]
    public async Task StopBegunButLogFrozen_StopsCountingOnlyWhenBothClocksAgree()
    {
        // сервер убили посреди остановки: «Stopped the server!» так и не появилась; файл менялся полминуты назад
        PutLog(Main, HostedLogSamples.Stopping, _time.UtcNow - TimeSpan.FromSeconds(30));
        var logs = Logs();

        Assert.Equal(HostedServerState.Stopping, (await logs.PollAsync(default)).State);
        // по MLSD файл уже не менялся 2,5 минуты, но мы смотрим на него только минуту — ещё не верим
        _time.UtcNow += TimeSpan.FromMinutes(1);
        Assert.Equal(HostedServerState.Stopping, (await logs.PollAsync(default)).State);

        _time.UtcNow += TimeSpan.FromMinutes(2);
        var update = await logs.PollAsync(default);

        Assert.Equal(HostedServerState.Stopped, update.State);
        Assert.True(HostedLogs.IsSurelyDown(update.State));
    }

    [Fact]
    public async Task StopBegun_FileUnchangedForLong_IsStoppedAtOnce_EvenOnTheFirstLook()
    {
        // окно только открыли, а сервер убили посреди остановки полчаса назад: ждать своих 2 минут незачем
        PutLog(Main, HostedLogSamples.Stopping, _time.UtcNow - TimeSpan.FromMinutes(30));

        var update = await Logs().PollAsync(default);

        Assert.Equal(HostedServerState.Stopped, update.State);
    }

    [Fact]
    public async Task StopBegun_LogStillGrowing_StaysStopping()
    {
        var local = PutLog(Main, HostedLogSamples.Stopping);
        var logs = Logs();
        await logs.PollAsync(default);

        _time.UtcNow += TimeSpan.FromMinutes(3);
        Append(local, ["17.10.2026 13:34:00 [Event] Saved 99000 generating chunks..."]);
        File.SetLastWriteTimeUtc(local, _time.UtcNow);

        Assert.Equal(HostedServerState.Stopping, (await logs.PollAsync(default)).State);
    }

    [Fact]
    public async Task NoLogsYet_IsUnknown_AndTheStopQuestionStays()
    {
        var logs = Logs();

        var update = await logs.PollAsync(default);

        Assert.Equal(HostedServerState.Unknown, update.State);
        Assert.Empty(update.NewLines);
        Assert.Equal(HostedServerState.Unknown, await logs.CheckNowAsync(default));
        Assert.False(HostedLogs.IsSurelyDown(HostedServerState.Unknown));
    }

    [Fact]
    public async Task HugeLog_FirstReadTakesOnlyTheEnd()
    {
        var filler = Enumerable.Range(0, 9000).Select(i => $"17.10.2026 13:35:{i % 60:00} [Warning] Server overloaded. A tick took {500 + i % 100}ms to complete.");
        string[] lines = [.. HostedLogSamples.Running, .. filler];
        PutLog(Main, lines);
        var logs = Logs();

        var update = await logs.PollAsync(default);

        Assert.InRange(HostedLogSamples.Bytes(update.NewLines).Length, HostedLogs.FirstReadLimit - 200, HostedLogs.FirstReadLimit);
        Assert.All(update.NewLines, l => Assert.StartsWith("17.10.2026 13:35:", l)); // обрезанная первая строка пропущена
        Assert.Equal(lines[^1], update.NewLines[^1]);
        Assert.Equal(HostedServerState.Running, update.State); // начала не видно — считаем запущенным
        Assert.Null(logs.RunName);
    }

    [Fact]
    public async Task ServerWithoutRest_TailComesFromTheWholeFile()
    {
        _host.Server.NoRange = true;
        var local = PutLog(Main, HostedLogSamples.Running);
        var logs = Logs();
        await logs.PollAsync(default);

        Append(local, HostedLogSamples.Stopped[HostedLogSamples.Running.Length..]);
        var update = await logs.PollAsync(default);

        Assert.Equal(HostedLogSamples.Stopped[HostedLogSamples.Running.Length..], update.NewLines);
        Assert.Equal(HostedServerState.Stopped, update.State);
    }

    [Fact]
    public async Task DebugLog_IsNeverRead()
    {
        PutLog(Main, HostedLogSamples.Running);
        _host.Server.Put("/Logs/server-debug.log", new string('d', 300_000));
        _host.Server.Put("/Logs/Archive/2026-10-17_13_25_13/server-debug.log", new string('d', 300_000));
        PutLog("/Logs/Archive/2026-10-17_13_25_13/server-main.log", HostedLogSamples.Archived);
        var reads = TrackReads();
        var logs = Logs();

        await logs.PollAsync(default);
        var runs = await logs.RunsAsync(default);
        await logs.ReadRunAsync(runs[0], default);
        await logs.FindCrashAsync(default);

        Assert.DoesNotContain(reads, p => p.EndsWith("server-debug.log", StringComparison.Ordinal));
    }

    // ---------- перезапуск

    [Fact]
    public async Task Restart_LogBecameSmaller_ReadFromTheStart()
    {
        var local = PutLog(Main, HostedLogSamples.Stopped);
        var logs = Logs();
        Assert.Equal(HostedServerState.Stopped, (await logs.PollAsync(default)).State);

        // при запуске сервер уносит прошлый лог в архив и начинает новый
        _host.Server.Put("/Logs/Archive/2026-10-17_13_27_08/server-main.log", File.ReadAllBytes(local));
        var fresh = NextDay(HostedLogSamples.Starting);
        PutLog(Main, fresh);
        var update = await logs.PollAsync(default);

        Assert.True(update.Restarted);
        Assert.Equal(fresh, update.NewLines);
        Assert.Equal(fresh, logs.Lines);
        Assert.Equal(HostedServerState.Starting, update.State);
        Assert.Equal("2026-10-18_13_27_08", logs.RunName);
        Assert.Empty(update.Players);
    }

    [Fact]
    public async Task Restart_NewLogAlreadyBiggerThanTheOld_SeenByTheArchiveFolder()
    {
        PutLog(Main, HostedLogSamples.Starting);
        _host.Server.Put("/Logs/Archive/2026-10-17_10_44_24/server-main.log", "old run\n");
        var archive = Path.Combine(_host.Server.Root, "Logs", "Archive");
        Directory.SetLastWriteTimeUtc(archive, new DateTime(2026, 10, 17, 8, 44, 24, DateTimeKind.Utc));
        var logs = Logs();
        await logs.PollAsync(default);

        // опрос пропустил перезапуск, а новый лог успел вырасти больше прежнего
        _host.Server.Put("/Logs/Archive/2026-10-17_13_27_08/server-main.log", HostedLogSamples.Bytes(HostedLogSamples.Starting));
        Directory.SetLastWriteTimeUtc(archive, new DateTime(2026, 10, 18, 11, 27, 8, DateTimeKind.Utc));
        var fresh = NextDay(HostedLogSamples.Stopped);
        PutLog(Main, fresh);
        var update = await logs.PollAsync(default);

        Assert.True(update.Restarted);
        Assert.Equal(fresh, update.NewLines); // с начала нового файла, а не с позиции прежнего
        Assert.Equal(HostedServerState.Stopped, update.State);
    }

    [Fact]
    public async Task Restart_LogVanishedAndCameBack()
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        var logs = Logs();
        await logs.PollAsync(default);

        File.Delete(local);
        var gone = await logs.PollAsync(default);
        Assert.Equal(HostedServerState.Unknown, gone.State);
        Assert.Empty(gone.Players);

        var fresh = NextDay(HostedLogSamples.Stopped); // больше прежнего: по размеру перезапуск не виден
        PutLog(Main, fresh);
        var update = await logs.PollAsync(default);

        Assert.True(update.Restarted);
        Assert.Equal(fresh, update.NewLines);
    }

    [Fact]
    public async Task LogGoneBetweenListingAndRead_IsLikeNoLog_ThenARestart()
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        var logs = Logs();
        await logs.PollAsync(default);
        Append(local, ["17.10.2026 13:31:20 [Notification] one more"]);
        _host.Server.OnBefore = (op, path) =>
        {
            if (op != "read" || path != Main) return;
            _host.Server.OnBefore = null;
            File.Delete(local); // сервер уносит лог в архив, пока мы между листингом и чтением
        };

        Assert.Equal(HostedServerState.Unknown, (await logs.PollAsync(default)).State);

        PutLog(Main, NextDay(HostedLogSamples.Starting));
        Assert.True((await logs.PollAsync(default)).Restarted);
    }

    [Fact]
    public async Task Restart_ArchiveFolderComesBeforeTheLogMoves_RestartOnlyOnceWithTheNewLog()
    {
        var local = PutLog(Main, HostedLogSamples.Stopped);
        PutLog("/Logs/Archive/2026-10-17_13_25_13/server-main.log", HostedLogSamples.Archived);
        var archive = Path.Combine(_host.Server.Root, "Logs", "Archive");
        Directory.SetLastWriteTimeUtc(archive, new DateTime(2026, 10, 17, 11, 27, 8, DateTimeKind.Utc));
        var logs = Logs();
        await logs.PollAsync(default);

        // сервер запускается: папку в архиве уже завёл, а прошлый лог ещё не унёс
        _host.Server.MakeDir("/Logs/Archive/2026-10-17_13_27_08");
        Directory.SetLastWriteTimeUtc(archive, new DateTime(2026, 10, 18, 11, 27, 8, DateTimeKind.Utc));
        var moving = await logs.PollAsync(default);

        Assert.False(moving.Restarted); // прежний лог не читается заново как новый запуск
        Assert.Empty(moving.NewLines);

        // унёс, новый лог того же размера: перезапуск виден только по папке архива
        _host.Server.Put("/Logs/Archive/2026-10-17_13_27_08/server-main.log", File.ReadAllBytes(local));
        var fresh = NextDay(HostedLogSamples.Stopped);
        PutLog(Main, fresh);
        var update = await logs.PollAsync(default);

        Assert.True(update.Restarted);
        Assert.Equal(fresh, update.NewLines);
        Assert.False((await logs.PollAsync(default)).Restarted);
    }

    // ---------- вылет текущего запуска

    private static readonly string[] FatalLines =
    [
        "17.10.2026 13:40:00 [Fatal] Game Version: v1.22.7 (Stable)",
        "Critical error occurred in the following mod: badmod@1.0.0",
        "System.InvalidOperationException: boom",
        "   at BadMod.Boom.Now(String where)",
        "Crash written to file at \"/home/sid_1234567/vintageStory/Logs/server-crash.log\"",
    ];

    private const string Report = "Game Version: v1.22.7 (Stable)\n17.10.2026 13:40:00: Critical error occurred in the following mod: badmod@1.0.0\n"
                                  + "System.InvalidOperationException: boom\n   at BadMod.Boom.Now(String where)\n";

    private static readonly DateTime CrashUtc = new(2026, 10, 17, 11, 40, 0, DateTimeKind.Utc); // 13:40:00 по часам сервера

    private static readonly LocalMod BadMod =
        ModScanner.FromModInfo("/Mods/badmod_1.0.0.zip", """{ "type": "code", "modid": "badmod", "name": "Bad Mod", "version": "1.0.0" }""");

    [Fact]
    public async Task FreshCrashLog_ServerCrashed()
    {
        PutLog(Main, HostedLogSamples.Running);
        File.SetLastWriteTimeUtc(_host.Server.Put("/Logs/server-crash.log", Report), new DateTime(2026, 10, 17, 11, 31, 20, DateTimeKind.Utc));

        var update = await Logs().PollAsync(default);

        Assert.Equal(HostedServerState.Crashed, update.State);
        Assert.True(HostedLogs.IsSurelyDown(update.State));
        Assert.Empty(update.Players);
    }

    [Fact]
    public async Task OldCrashLog_OfAnEarlierRun_IsIgnored()
    {
        PutLog(Main, HostedLogSamples.Running);
        // 11:00 по часам сервера — раньше начала запуска (13:27:08): этот отчёт остался от прошлых запусков
        File.SetLastWriteTimeUtc(_host.Server.Put("/Logs/server-crash.log", Report), new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc));

        Assert.Equal(HostedServerState.Running, (await Logs().PollAsync(default)).State);
    }

    [Fact]
    public async Task CrashLines_WithoutCrashLog_ServerCrashed()
    {
        PutLog(Main, [.. HostedLogSamples.Running, "17.10.2026 13:40:00 [Error] More then 100000 errors detected. Shutting down now."]);

        Assert.Equal(HostedServerState.Crashed, (await Logs().PollAsync(default)).State);
    }

    // время из LIST — до минуты: вылет прошлого запуска и автоперезапуск за секунды попадают в одну минуту
    private static readonly string[] StartedAt134030 =
    [
        "17.10.2026 13:40:30 [Notification] Server logger started.",
        "17.10.2026 13:40:40 [Event] Dedicated Server now running on Port 29600 and ip 198.51.100.10!",
    ];

    [Fact]
    public async Task ListTime_CrashLogOfThePreviousRun_InTheMinuteOfTheStart_IsNotThisRunsCrash()
    {
        _host.Server.ListLocalTime = true;
        PutLog(Main, StartedAt134030, LocalClock(13, 40, 40));
        File.SetLastWriteTimeUtc(_host.Server.Put("/Logs/server-crash.log", Report), LocalClock(13, 40, 5));

        var update = await Logs().PollAsync(default);

        Assert.Equal(HostedServerState.Running, update.State);
        Assert.False(HostedLogs.IsSurelyDown(update.State));
    }

    [Fact]
    public async Task ListTime_CrashLogInTheNextMinute_IsThisRunsCrash()
    {
        _host.Server.ListLocalTime = true;
        PutLog(Main, StartedAt134030, LocalClock(13, 40, 40));
        File.SetLastWriteTimeUtc(_host.Server.Put("/Logs/server-crash.log", Report), LocalClock(13, 41, 10));

        Assert.Equal(HostedServerState.Crashed, (await Logs().PollAsync(default)).State);
    }

    [Fact]
    public async Task HugeLog_CrashLogWrittenAfterTheFirstLineRead_IsThisRunsCrash()
    {
        // начала запуска не видно, строк вылета нет (процесс убит), отчёт записан, когда лог уже шёл
        PutLog(Main, [.. HostedLogSamples.Running, .. Overloaded()]);
        PutLog("/Logs/Archive/2026-10-17_13_25_13/server-main.log", HostedLogSamples.Archived);
        File.SetLastWriteTimeUtc(_host.Server.Put("/Logs/server-crash.log", Report), new DateTime(2026, 10, 17, 11, 36, 30, DateTimeKind.Utc));
        var logs = Logs();

        var crash = await logs.FindCrashAsync(default, [BadMod]);

        Assert.Equal(HostedServerState.Crashed, logs.State);
        Assert.NotNull(crash);
        Assert.True(crash.Current); // не прошлого запуска из архива
        Assert.Equal("badmod", crash.Info.ModId);
        var runs = await logs.RunsAsync(default);
        Assert.Null((await logs.ReadRunAsync(runs[0], default)).CrashLog);
    }

    [Fact]
    public async Task FindCrash_CurrentRun_WithReportAndCulprit_OnlyOnce()
    {
        PutLog(Main, [.. HostedLogSamples.Running, .. FatalLines], CrashUtc);
        File.SetLastWriteTimeUtc(_host.Server.Put("/Logs/server-crash.log", Report), CrashUtc);
        var logs = Logs();

        var crash = await logs.FindCrashAsync(default, [BadMod]);

        Assert.NotNull(crash);
        Assert.True(crash.Current);
        Assert.Equal("2026-10-17_13_27_08", crash.Run);
        Assert.Equal("/Logs/server-crash.log", crash.CrashLog);
        Assert.Equal(Report, crash.Report);
        Assert.Equal(GameExitKind.ServerDown, crash.Finding.Kind);
        Assert.Equal("badmod", crash.Info.ModId);
        Assert.Equal("Bad Mod", crash.Info.ModName);
        Assert.Equal("/Mods/badmod_1.0.0.zip", crash.Info.ModPath); // путь на FTP: по нему окно выключит мод
        Assert.Equal(CulpritSource.GameReport, crash.Info.Source);
        Assert.Equal(CrashUtc.ToLocalTime(), crash.Info.At);
        Assert.Equal(32, crash.Info.Id.Length);

        Assert.Null(await logs.FindCrashAsync(default, [BadMod])); // тот же вылет второй раз не отдаётся
        // окно, открытое заново, узнает его по Id
        Assert.Equal(crash.Info.Id, (await Logs().FindCrashAsync(default))?.Info.Id);
    }

    [Fact]
    public async Task CurrentCrashReported_AfterTheCrashIsGiven_UntilTheServerStartsAgain()
    {
        PutLog(Main, [.. HostedLogSamples.Running, .. FatalLines], CrashUtc);
        var logs = Logs();
        Assert.False(logs.CurrentCrashReported);   // лог ещё не читали

        Assert.Equal(HostedServerState.Crashed, (await logs.PollAsync(default)).State);
        Assert.False(logs.CurrentCrashReported);   // вылет виден, но ещё не отдан
        Assert.NotNull(await logs.FindCrashAsync(default));
        Assert.True(logs.CurrentCrashReported);    // окну незачем искать его на каждом опросе

        // новый запуск: прошлый лог уехал в архив, новый короче
        PutLog("/Logs/Archive/2026-10-17_13_27_08/server-main.log", [.. HostedLogSamples.Running, .. FatalLines], CrashUtc);
        PutLog(Main, NextDay(HostedLogSamples.Starting));
        Assert.True((await logs.PollAsync(default)).Restarted);
        Assert.False(logs.CurrentCrashReported);
    }

    [Fact]
    public async Task FindCrash_TakesModsFromTheLastScan()
    {
        _host.PutMod("/Mods/badmod_1.0.0.zip", "badmod", "1.0.0");
        await ModTargets.ScanAsync(_host.Target);
        PutLog(Main, [.. HostedLogSamples.Running, .. FatalLines], CrashUtc);

        var crash = await Logs().FindCrashAsync(default);

        Assert.Equal("/Mods/badmod_1.0.0.zip", crash?.Info.ModPath);
        Assert.Null(crash?.Report); // отчёта нет — разбор по строкам лога
    }

    [Fact]
    public async Task FindCrash_EmptyCrashLog_CulpritFromTheLogLines()
    {
        PutLog(Main, [.. HostedLogSamples.Running, .. FatalLines], CrashUtc);
        File.SetLastWriteTimeUtc(_host.Server.Put("/Logs/server-crash.log", ""), CrashUtc);

        var crash = await Logs().FindCrashAsync(default, [BadMod]);

        Assert.Equal("badmod", crash?.Info.ModId);
        Assert.Null(crash?.Report);
    }

    [Fact]
    public async Task FindCrash_CrashLogStillEmpty_WaitsForTheReportOnce()
    {
        // вылет виден только по свежему отчёту, а сервер его ещё пишет
        PutLog(Main, HostedLogSamples.Running);
        var report = _host.Server.Put("/Logs/server-crash.log", "");
        File.SetLastWriteTimeUtc(report, CrashUtc);
        var logs = Logs();

        Assert.Null(await logs.FindCrashAsync(default, [BadMod]));

        File.WriteAllText(report, Report);
        File.SetLastWriteTimeUtc(report, CrashUtc);
        var crash = await logs.FindCrashAsync(default, [BadMod]);

        Assert.Equal("badmod", crash?.Info.ModId);
        Assert.Equal(Report, crash?.Report);
    }

    [Fact]
    public async Task FindCrash_CrashLogStaysEmpty_TheCrashStillComes()
    {
        PutLog(Main, HostedLogSamples.Running);
        File.SetLastWriteTimeUtc(_host.Server.Put("/Logs/server-crash.log", ""), CrashUtc);
        var logs = Logs();

        Assert.Null(await logs.FindCrashAsync(default));

        Assert.NotNull(await logs.FindCrashAsync(default));
    }

    [Fact]
    public async Task FindCrash_SeveralReportsInTheCrashLog_TheCulpritOfTheLastOne()
    {
        const string old = "Game Version: v1.22.7 (Stable)\n16.10.2026 10:00:00: Critical error occurred in the following mod: oldmod@2.0.0\n"
                           + "System.Exception: old\n   at OldMod.Thing.Do()\n";
        PutLog(Main, [.. HostedLogSamples.Running, .. FatalLines], CrashUtc);
        File.SetLastWriteTimeUtc(_host.Server.Put("/Logs/server-crash.log", old + Report), CrashUtc);

        var crash = await Logs().FindCrashAsync(default, [BadMod]);

        Assert.Equal("badmod", crash?.Info.ModId);
        Assert.Equal(Report, crash?.Report);
    }

    [Fact]
    public async Task FindCrash_HugeLogWithoutStart_IsNotRepeatedAfterItGoesToTheArchive()
    {
        var local = PutLog(Main, [.. HostedLogSamples.Running, .. Overloaded(), .. FatalLines], CrashUtc);
        var logs = Logs();
        var crash = await logs.FindCrashAsync(default, [BadMod]);
        Assert.NotNull(crash);
        Assert.Null(logs.RunName); // начала не видно

        // хостер поднял сервер заново: упавший запуск уехал в архив под именем своего начала
        _host.Server.Put("/Logs/Archive/2026-10-17_13_27_08/server-main.log", File.ReadAllBytes(local));
        PutLog(Main, NextDay(HostedLogSamples.Starting));

        Assert.True((await logs.PollAsync(default)).Restarted);
        Assert.Null(await logs.FindCrashAsync(default, [BadMod]));
    }

    [Fact]
    public async Task FindCrash_HugeLogWithoutStart_NotRepeatedWhileTheLogIsGone()
    {
        var local = PutLog(Main, [.. HostedLogSamples.Running, .. Overloaded(), .. FatalLines], CrashUtc);
        var logs = Logs();
        Assert.NotNull(await logs.FindCrashAsync(default));

        // лог уже в архиве, новый ещё не начат
        _host.Server.Put("/Logs/Archive/2026-10-17_13_27_08/server-main.log", File.ReadAllBytes(local));
        File.Delete(local);

        Assert.Null(await logs.FindCrashAsync(default));
        PutLog(Main, NextDay(HostedLogSamples.Starting));
        Assert.Null(await logs.FindCrashAsync(default));
    }

    [Fact]
    public async Task FindCrash_NothingCrashed_Null()
    {
        PutLog(Main, HostedLogSamples.Stopped);
        PutLog("/Logs/Archive/2026-10-17_13_25_13/server-main.log", HostedLogSamples.Archived);
        var logs = Logs();

        Assert.Null(await logs.FindCrashAsync(default));
        Assert.Null(await logs.FindCrashAsync(default));
    }

    // ---------- вылет прошлого запуска (сервер уже перезапущен)

    [Fact]
    public async Task FindCrash_AfterRestart_InTheNewestArchiveFolder()
    {
        PutLog(Main, NextDay(HostedLogSamples.Starting));
        const string run = "/Logs/Archive/2026-10-17_13_27_08";
        PutLog(run + "/server-main.log", [.. HostedLogSamples.Running, .. FatalLines], CrashUtc);
        File.SetLastWriteTimeUtc(_host.Server.Put(run + "/server-crash.log", Report), CrashUtc);
        PutLog("/Logs/Archive/2026-10-17_13_25_13/server-main.log", HostedLogSamples.Archived);
        var logs = Logs();

        var crash = await logs.FindCrashAsync(default, [BadMod]);

        Assert.NotNull(crash);
        Assert.False(crash.Current);
        Assert.Equal("2026-10-17_13_27_08", crash.Run);
        Assert.Equal(run + "/server-crash.log", crash.CrashLog);
        Assert.Equal("badmod", crash.Info.ModId);
        Assert.Null(await logs.FindCrashAsync(default, [BadMod]));
    }

    [Fact]
    public async Task FindCrash_ReportedForTheCurrentRun_IsNotRepeatedAfterItGoesToTheArchive()
    {
        var local = PutLog(Main, [.. HostedLogSamples.Running, .. FatalLines], CrashUtc);
        var logs = Logs();
        Assert.NotNull(await logs.FindCrashAsync(default));

        // хостер поднял сервер заново: упавший запуск уехал в архив
        _host.Server.Put("/Logs/Archive/2026-10-17_13_27_08/server-main.log", File.ReadAllBytes(local));
        PutLog(Main, NextDay(HostedLogSamples.Starting));

        Assert.Null(await logs.FindCrashAsync(default));
    }

    [Fact]
    public async Task FindCrash_ArchivedRun_CrashLogLeftInLogs()
    {
        PutLog(Main, NextDay(HostedLogSamples.Starting));
        // упавший запуск без строк вылета (процесс убит), а отчёт остался в Logs: записан во время того запуска
        PutLog("/Logs/Archive/2026-10-17_13_27_08/server-main.log", HostedLogSamples.Running);
        File.SetLastWriteTimeUtc(_host.Server.Put("/Logs/server-crash.log", Report), new DateTime(2026, 10, 17, 11, 35, 0, DateTimeKind.Utc));
        var logs = Logs();

        var poll = await logs.PollAsync(default);
        var crash = await logs.FindCrashAsync(default, [BadMod]);

        Assert.Equal(HostedServerState.Starting, poll.State); // текущему запуску чужой отчёт не приписан
        Assert.NotNull(crash);
        Assert.False(crash.Current);
        Assert.Equal("/Logs/server-crash.log", crash.CrashLog);
        Assert.Equal("badmod", crash.Info.ModId);
    }

    // ---------- архив

    [Fact]
    public async Task Runs_NewestFirst_WithTheirSpans_AndReadOnDemand()
    {
        PutLog(Main, HostedLogSamples.Running);
        PutLog("/Logs/Archive/2026-10-17_13_25_13/server-main.log", HostedLogSamples.Archived);
        PutLog("/Logs/Archive/2026-10-17_10_44_24/server-main.log", ["17.10.2026 10:44:24 [Notification] Server logger started."]);
        _host.Server.MakeDir("/Logs/Archive/not-a-run");
        var logs = Logs();
        await logs.PollAsync(default);
        var reads = TrackReads();

        var runs = await logs.RunsAsync(default);

        Assert.Equal(["2026-10-17_13_25_13", "2026-10-17_10_44_24"], runs.Select(r => r.Name));
        Assert.Equal(new DateTime(2026, 10, 17, 13, 25, 13), runs[0].Started);
        Assert.Equal(new DateTime(2026, 10, 17, 13, 27, 8), runs[0].Until); // дальше — текущий запуск
        Assert.Equal(new DateTime(2026, 10, 17, 13, 25, 13), runs[1].Until);
        Assert.Empty(reads); // список — одним листингом, логи не качаются

        var run = await logs.ReadRunAsync(runs[0], default);

        Assert.Equal(HostedLogSamples.Archived, run.Lines);
        Assert.Equal(HostedServerState.Stopped, run.Outcome);
        Assert.False(run.Truncated);
        Assert.Null(run.CrashLog);
        Assert.Equal(["/Logs/Archive/2026-10-17_13_25_13/server-main.log"], reads.Distinct());
        Assert.Equal(HostedServerState.Unknown, (await logs.ReadRunAsync(runs[1], default)).Outcome); // оборвался без остановки
    }

    [Fact]
    public async Task Runs_NoArchive_Empty()
    {
        PutLog(Main, HostedLogSamples.Running);

        Assert.Empty(await Logs().RunsAsync(default));
    }

    // ---------- раскладка

    [Fact]
    public async Task LogsDir_FromThePinnedConfig_WithoutLookingForTheLayout()
    {
        _host.Settings.ConfigPath = "data/serverconfig.json";
        _host.Server.Put("/data/serverconfig.json", "{}");
        PutLog("/data/Logs/server-main.log", HostedLogSamples.Running);
        var reads = TrackReads();
        var logs = Logs();

        var update = await logs.PollAsync(default);

        Assert.Equal("/data/Logs", logs.LogsDir);
        Assert.Equal(HostedServerState.Running, update.State);
        Assert.Equal(["/data/Logs/server-main.log"], reads); // конфиг не читался
    }

    // ---------- сеть

    [Fact]
    public async Task DroppedConnection_PollIsRetriedOnce()
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        var logs = Logs();
        await logs.PollAsync(default);
        Append(local, ["17.10.2026 13:31:20 [Notification] still here"]);
        _host.Server.DropNext = true;

        var update = await logs.PollAsync(default);

        Assert.Equal(["17.10.2026 13:31:20 [Notification] still here"], update.NewLines);
        Assert.Equal(2, _host.Server.Connects);
    }

    [Fact]
    public async Task ServerUnreachable_PollThrowsAndKeepsTheState_QuickCheckSaysUnknown()
    {
        PutLog(Main, HostedLogSamples.Stopped);
        var logs = Logs();
        await logs.PollAsync(default);
        _host.Server.FailOn = (op, _) => op is "list" or "connect";
        _host.Server.FailKind = RemoteFailure.Connect;

        await Assert.ThrowsAsync<RemoteFilesException>(() => logs.PollAsync(default));

        Assert.Equal(HostedServerState.Stopped, logs.State);
        Assert.Equal(HostedServerState.Unknown, await logs.CheckNowAsync(default)); // вопрос «останови сервер» — как раньше
    }

    [Fact]
    public async Task QuickCheck_Stopped()
    {
        PutLog(Main, HostedLogSamples.Stopped);

        Assert.Equal(HostedServerState.Stopped, await Logs().CheckNowAsync(default));
    }

    [Fact]
    public async Task QuickCheck_QueueBusyTooLong_Unknown()
    {
        PutLog(Main, HostedLogSamples.Stopped);
        var release = new TaskCompletionSource();
        var busy = HostedSessions.UseAsync(_host.Profile.Id, _host.Endpoint, async _ =>
        {
            await release.Task;
            return 0;
        }, default);

        var state = await Logs().CheckNowAsync(default, TimeSpan.FromMilliseconds(200));

        release.SetResult();
        await busy;
        Assert.Equal(HostedServerState.Unknown, state);
    }
}

/// <summary>Часы для тестов: время двигает сам тест.</summary>
internal sealed class FakeTime(DateTime utc) : TimeProvider
{
    public DateTime UtcNow { get; set; } = utc;

    public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
}

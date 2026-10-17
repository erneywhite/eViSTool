using eViSTool.Core.Diagnostics;
using eViSTool.Core.Hosting;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>Признаки запуска сервера по строкам server-main.log — на настоящих логах G-Portal.</summary>
public sealed class HostedRunMarksTests
{
    private static HostedRunMarks Read(IEnumerable<string> lines, bool fromMiddle = false)
    {
        var marks = new HostedRunMarks(fromMiddle);
        foreach (var line in lines) marks.Add(line);
        return marks;
    }

    [Fact]
    public void StoppedRun_IsStopped_WithStartTakenFromTheFirstLine()
    {
        var marks = Read(HostedLogSamples.Stopped);

        Assert.Equal(HostedServerState.Stopped, marks.State(stale: false));
        Assert.True(marks.Ready);
        Assert.True(marks.StopBegun);
        Assert.False(marks.Crashed);
        Assert.Equal(new DateTime(2026, 10, 17, 13, 27, 8), marks.Started);
        Assert.Equal(DateTimeKind.Unspecified, marks.Started!.Value.Kind); // время сервера, не UTC и не этой машины
        Assert.Equal(new DateTime(2026, 10, 17, 13, 32, 2), marks.LastTime);
        Assert.Equal("2026-10-17_13_27_08", marks.RunName); // так назовётся его папка в архиве
    }

    [Fact]
    public void RunningStartingAndStopping_FromTheSameLog()
    {
        Assert.Equal(HostedServerState.Running, Read(HostedLogSamples.Running).State(stale: false));
        Assert.Equal(HostedServerState.Starting, Read(HostedLogSamples.Starting).State(stale: false));
        Assert.Equal(HostedServerState.Starting, Read([]).State(stale: false)); // лог есть, но пуст

        var stopping = Read(HostedLogSamples.Stopping);
        Assert.Equal(HostedServerState.Stopping, stopping.State(stale: false));
        // остановка начата, а лог давно не растёт — сервер убили посреди неё
        Assert.Equal(HostedServerState.Stopped, stopping.State(stale: true));
        // запущенный сервер «застывает» в Running и по давности не становится остановленным
        Assert.Equal(HostedServerState.Running, Read(HostedLogSamples.Running).State(stale: true));
    }

    [Theory]
    [InlineData("17.10.2026 13:40:00 [Error] More then 100000 errors detected. Shutting down now. Threshold can be changed in serverconfig.json \"DieAboveErrorCount\"")]
    [InlineData("17.10.2026 13:40:00 [Fatal] Game Version: v1.22.7 (Stable)")]
    [InlineData("17.10.2026 13:40:00 [Server Fatal] System.NullReferenceException: boom")]
    [InlineData("Critical error occurred in the following mod: badmod@1.0.0")]
    [InlineData("Crash written to file at \"/home/container/Logs/server-crash.log\"")]
    public void CrashLines_MakeTheRunCrashed_EvenIfItStoppedAfterwards(string crashLine)
    {
        var marks = Read([.. HostedLogSamples.Running, crashLine, "17.10.2026 13:40:02 [Event] Stopped the server!"]);

        Assert.True(marks.Crashed);
        Assert.Equal(HostedServerState.Crashed, marks.State(stale: false));
    }

    [Fact]
    public void OrdinaryErrors_AreNotACrash()
    {
        var marks = Read([.. HostedLogSamples.Running,
            "17.10.2026 13:40:00 [Error] Exception: boom in a tick listener",
            "   at CrashTestMod.Boom.Now(String where)"]);

        Assert.False(marks.Crashed);
        Assert.Equal(HostedServerState.Running, marks.State(stale: false));
    }

    [Fact]
    public void ChatSayingStopOrCrash_ChangesNothing()
    {
        // игрок пишет в чат что угодно — по этому нельзя решить, что сервер остановлен, и не спросить «останови сервер»
        var marks = Read([.. HostedLogSamples.Running,
            "17.10.2026 13:40:01 [Chat] 0 | Erney: Stopped the server!",
            "17.10.2026 13:40:02 [Chat] 0 | Erney: More then 100000 errors detected. Shutting down now",
            "17.10.2026 13:40:03 [Chat] 0 | Erney: Server stop requested"]);

        Assert.Equal(HostedServerState.Running, marks.State(stale: true));
        Assert.Equal(new DateTime(2026, 10, 17, 13, 40, 3), marks.LastTime); // время строки чата всё равно учтено
    }

    [Fact]
    public void ModRelayingChatIntoTheLog_ChangesNothing()
    {
        // мост чата (Discord и т. п.) пишет сообщения игроков обычной записью мода, не [Chat]
        var marks = Read([.. HostedLogSamples.Running,
            "17.10.2026 13:40:01 [Notification] [DiscordBridge] Erney: Stopped the server!",
            "17.10.2026 13:40:02 [Event] Erney: Stopped the server!",
            "17.10.2026 13:40:03 [Notification] [DiscordBridge] Erney: Server stop requested",
            "17.10.2026 13:40:04 [Notification] <Erney> Server termination event SIGTERM received",
            "17.10.2026 13:40:05 [Notification] [DiscordBridge] Erney: Critical error occurred in the following mod: badmod@1.0.0",
            "17.10.2026 13:40:06 [Notification] [DiscordBridge] Erney: More then 100000 errors detected. Shutting down now",
            "17.10.2026 13:40:07 [Warning] [DiscordBridge] Crash written to file at \"/tmp/x.log\""]);

        Assert.False(marks.StopBegun);
        Assert.False(marks.Stopped);
        Assert.False(marks.Crashed);
        Assert.Equal(HostedServerState.Running, marks.State(stale: true));
    }

    [Fact]
    public void FromTheMiddle_StartIsUnknown_ButTheServerCountsAsRunning()
    {
        var marks = Read(HostedLogSamples.Running.Skip(200), fromMiddle: true);

        Assert.Null(marks.Started);
        Assert.Null(marks.RunName);
        Assert.Equal(HostedServerState.Running, marks.State(stale: false));
        Assert.Equal(new DateTime(2026, 10, 17, 13, 31, 18), marks.LastTime);
    }

    [Fact]
    public void FirstTime_IsKnownEvenFromTheMiddle()
    {
        var lines = HostedLogSamples.Running.Skip(200).ToArray();
        var first = HostedRunMarks.ParseTime(GameLog.Parse(lines)[0].Time);

        Assert.NotNull(first);
        Assert.Equal(first, Read(lines, fromMiddle: true).FirstTime);
        Assert.Equal(new DateTime(2026, 10, 17, 13, 27, 8), Read(HostedLogSamples.Running).FirstTime);
    }

    [Fact]
    public void Add_ReturnsTheLineTime_OrThePreviousOneForStackLines()
    {
        var marks = new HostedRunMarks();

        Assert.Equal(new DateTime(2026, 10, 17, 13, 27, 8), marks.Add("17.10.2026 13:27:08 [Notification] Server logger started."));
        Assert.Equal(new DateTime(2026, 10, 17, 13, 27, 8), marks.Add("   at Vintagestory.Server.ServerMain.Process()"));
        Assert.Null(new HostedRunMarks().Add("<code>/help</code> : without a time"));
    }

    [Theory]
    [InlineData("17.10.2026 13:27:08", 2026, 10, 17, 13, 27, 8)]
    [InlineData("7.10.2026 00:18:55", 2026, 10, 7, 0, 18, 55)]
    [InlineData("10/17/2026 1:27:08 PM", 2026, 10, 17, 13, 27, 8)]
    [InlineData("17/10/2026 13:27:08", 2026, 10, 17, 13, 27, 8)]
    [InlineData("2026-10-17 13:27:08", 2026, 10, 17, 13, 27, 8)]
    public void ParseTime_KnowsCommonServerDateFormats(string text, int y, int mo, int d, int h, int mi, int s)
    {
        Assert.Equal(new DateTime(y, mo, d, h, mi, s), HostedRunMarks.ParseTime(text));
    }

    [Fact]
    public void ParseTime_Garbage_IsNull() => Assert.Null(HostedRunMarks.ParseTime("yesterday 13:27"));

    [Fact]
    public void RunName_RoundTrips()
    {
        Assert.True(HostedRunMarks.TryParseRunName("2026-10-17_13_25_13", out var started));
        Assert.Equal(new DateTime(2026, 10, 17, 13, 25, 13), started);
        Assert.False(HostedRunMarks.TryParseRunName("old-logs", out _));
    }
}

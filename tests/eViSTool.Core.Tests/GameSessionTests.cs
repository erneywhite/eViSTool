using eViSTool.Core.Diagnostics;
using eViSTool.Core.Mods;

namespace eViSTool.Core.Tests;

/// <summary>Наблюдение за запуском игры: логи дописываются по ходу, вылет — после выхода, закрытие мира — сразу.</summary>
public sealed class GameSessionTests : IDisposable
{
    private static readonly string Data = Path.Combine(AppContext.BaseDirectory, "TestData", "Crash");
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-session-" + Guid.NewGuid().ToString("N"))).FullName;
    private string Logs => Directory.CreateDirectory(Path.Combine(_root, "Logs")).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ModFingerprints Mods()
    {
        var zip = Path.Combine(_root, "CrashTest_1.0.0.zip");
        if (!File.Exists(zip)) File.Copy(Path.Combine(Data, "CrashTest_1.0.0.zip"), zip);
        return ModFingerprints.Build([ModScanner.ReadZip(zip)]);
    }

    private static string[] Sample(string name) => File.ReadAllLines(Path.Combine(Data, name));

    private void Append(string file, IEnumerable<string> lines) => File.AppendAllLines(Path.Combine(Logs, file), lines);

    [Fact]
    public void LogTail_ReadsOnlyNewWholeLines_AndStartsOverOnANewFile()
    {
        // LogTail — для окна на Windows: новый файл лога узнаётся по времени создания, а на Linux .NET отдаёт вместо него
        // время последнего изменения (агент на Linux ошибки модов берёт из вывода сервера, не из логов)
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Logs, "x.log");
        File.WriteAllText(path, "old\n");
        var tail = new LogTail(path, fromStart: false);
        Assert.Empty(tail.ReadNew());

        File.AppendAllText(path, "one\ntw");
        Assert.Equal(["one"], tail.ReadNew()); // «tw» ещё дописывается
        File.AppendAllText(path, "o\n");
        Assert.Equal(["two"], tail.ReadNew());

        File.WriteAllText(path, "new\n"); // прежний лог ушёл в архив, начат новый
        Assert.Equal(["new"], tail.ReadNew());
    }

    [Fact]
    public void WorldClosedByTheBuiltInServer_IsReportedWhileTheGameStillRuns()
    {
        var client = Sample("client-main-disconnected.log");
        var exitAt = Array.FindIndex(client, l => l.Contains("disconnected screen"));
        var session = new GameSession(_root, DateTime.UtcNow.AddMinutes(-1));

        Append("client-main.log", client[..exitAt]);
        Append("server-main.log", Sample("server-main-disconnected.log"));
        Assert.Null(session.Poll(Mods)); // мир ещё идёт

        Append("client-main.log", client[exitAt..]);
        var finding = session.Poll(Mods)!;
        Assert.Equal(GameExitKind.WorldClosed, finding.Kind);
        Assert.Equal("crashtest", finding.Culprit!.ModId);

        Assert.Null(session.Poll(Mods)); // сообщено один раз
        Assert.Null(session.Finish(Mods)); // и игру потом закрыли как обычно
    }

    [Fact]
    public void GameCrash_IsReportedAfterTheGameExits()
    {
        File.WriteAllText(Path.Combine(Logs, "client-crash.log"), "старый отчёт прошлого запуска");
        File.SetLastWriteTimeUtc(Path.Combine(Logs, "client-crash.log"), DateTime.UtcNow.AddHours(-1));
        var session = new GameSession(_root, DateTime.UtcNow.AddMinutes(-1));

        Append("client-main.log", Sample("client-main-crash.log"));
        File.Copy(Path.Combine(Data, "client-crash.log"), Path.Combine(Logs, "client-crash.log"), overwrite: true);
        Assert.Null(session.Poll(Mods)); // сам вылет без экрана отключения — ждём выхода процесса

        var finding = session.Finish(Mods)!;
        Assert.Equal(GameExitKind.Crashed, finding.Kind);
        Assert.Equal(CulpritSource.GameReport, finding.Culprit!.Source);
    }

    [Fact]
    public void OldCrashReportAndNormalExit_AreNotACrash()
    {
        File.WriteAllText(Path.Combine(Logs, "client-crash.log"), File.ReadAllText(Path.Combine(Data, "client-crash.log")));
        File.SetLastWriteTimeUtc(Path.Combine(Logs, "client-crash.log"), DateTime.UtcNow.AddHours(-1));
        var session = new GameSession(_root, DateTime.UtcNow.AddMinutes(-1));

        Append("client-main.log", [
            "7.10.2026 00:15:00 [Notification] Exiting current game to main menu, reason: leave world button pressed",
            "7.10.2026 00:15:01 [Notification] Exiting game now. Server running=False. Exit reason: Main screen quit button was pressed",
        ]);

        Assert.Null(session.Poll(Mods));
        Assert.Null(session.Finish(Mods));
    }
}

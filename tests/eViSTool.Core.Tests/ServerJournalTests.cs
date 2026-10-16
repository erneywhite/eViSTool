using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

/// <summary>Журнал действий с сервером: какие запросы туда идут, хранение 30 дней, устойчивость к битым строкам.</summary>
public sealed class ServerJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evistool-journal-" + Guid.NewGuid().ToString("N"));
    private string File1 => Path.Combine(_dir, "p.journal.jsonl");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("POST", "/start", null, null, "start", "")]
    [InlineData("POST", "/command", """{"Text":" /time set day "}""", null, "command", "/time set day")]
    [InlineData("POST", "/mods/toggle", """{"Path":"C:\\Server\\Mods\\carryon_1.0.0.zip","Enabled":false}""", null, "modOff", "carryon_1.0.0.zip")]
    [InlineData("POST", "/mods/toggle", """{"Path":"/srv/vs/Mods/carryon_1.0.0.zip","Enabled":true}""", null, "modOn", "carryon_1.0.0.zip")]
    [InlineData("POST", "/mods/install", null, "carry%20on_1.1.0.zip", "modInstall", "carry on_1.1.0.zip")]
    [InlineData("POST", "/backups/restore", """{"Name":"Survival-2026-10-16.vcdbs"}""", null, "restore", "Survival-2026-10-16.vcdbs")]
    [InlineData("PUT", "/config", "{}", null, "config", "")]
    [InlineData("POST", "/self-update", """{"Version":"0.11.5"}""", null, "selfUpdate", "0.11.5")]
    public void Describe_ChangingRequests(string method, string path, string? body, string? file, string action, string arg)
    {
        var (got, args) = ServerJournal.Describe(method, path, body, file)!.Value;
        Assert.Equal(action, got);
        Assert.Equal(arg, args.FirstOrDefault() ?? "");
    }

    [Theory]
    [InlineData("GET", "/status")]
    [InlineData("GET", "/mods")]
    [InlineData("GET", "/config")]
    [InlineData("POST", "/modconfig/read")] // POST, но только чтение
    [InlineData("POST", "/fs/dirs")]
    [InlineData("POST", "/backups/check-dir")]
    [InlineData("POST", "/notify/test")]
    public void Describe_ReadingIsNotJournaled(string method, string path) =>
        Assert.Null(ServerJournal.Describe(method, path, null, null));

    [Fact]
    public void Describe_BrokenBody_StillNamesTheAction()
    {
        var (action, args) = ServerJournal.Describe("POST", "/command", "{не json", null)!.Value;
        Assert.Equal(("command", ""), (action, args[0]));
    }

    [Fact]
    public void Read_NewestFirst_SkipsBrokenLinesAndOlderThan30Days()
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        ServerJournal.Append(File1, new JournalEntry(now.AddDays(-40), "start", [], "PC", null, false));
        ServerJournal.Append(File1, new JournalEntry(now.AddHours(-2), "stop", [], "PC", null, false));
        File.AppendAllText(File1, "{оборванная строка\n");
        ServerJournal.Append(File1, new JournalEntry(now.AddMinutes(-5), "command", ["/time"], "FRIEND", "10.0.0.7", true));

        var entries = ServerJournal.Read(File1, now);

        Assert.Equal(["command", "stop"], entries.Select(e => e.Action));
        var remote = entries[0];
        Assert.Equal(("FRIEND", "10.0.0.7", true, "/time"), (remote.Who, remote.Ip, remote.Remote, remote.Args[0]));
        Assert.Equal(DateTimeKind.Utc, remote.At.Kind);
    }

    [Fact]
    public void Prune_DropsOnlyOldEntries()
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        ServerJournal.Append(File1, new JournalEntry(now.AddDays(-31), "start", [], null, null, false));
        ServerJournal.Append(File1, new JournalEntry(now.AddDays(-1), "stop", [], null, null, false));

        ServerJournal.Prune(File1, now);

        Assert.Single(File.ReadAllLines(File1));
        Assert.Equal("stop", Assert.Single(ServerJournal.Read(File1, now)).Action);
    }

    [Fact]
    public void Read_NoFile_IsEmpty() => Assert.Empty(ServerJournal.Read(File1, DateTime.UtcNow));
}

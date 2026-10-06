using System.IO.Compression;
using eViSTool.Core.Diagnostics;
using eViSTool.Core.Mods;

namespace eViSTool.Core.Tests;

/// <summary>«Ошибки модов» за запуск: какие моды сколько ошибок дали (игра их проглатывает, но они копятся).</summary>
public sealed class ModErrorReportTests : IDisposable
{
    private static readonly string Data = Path.Combine(AppContext.BaseDirectory, "TestData", "Crash");
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-moderr-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _profileId = "test-" + Guid.NewGuid().ToString("N")[..8];

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        if (File.Exists(ModErrorReport.FileFor(_profileId))) File.Delete(ModErrorReport.FileFor(_profileId));
    }

    private ModFingerprints Mods(params string[] extra)
    {
        var zip = Path.Combine(_root, "CrashTest_1.0.0.zip");
        if (!File.Exists(zip)) File.Copy(Path.Combine(Data, "CrashTest_1.0.0.zip"), zip);
        return ModFingerprints.Build([ModScanner.ReadZip(zip), .. extra.Select(ModScanner.ReadZip)]);
    }

    private string LangMod(string modId, params string[] strings)
    {
        var path = Path.Combine(_root, modId + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open()))
            w.Write($$"""{ "modid": "{{modId}}", "name": "Prospecting Scan", "version": "1.2.0" }""");
        using (var w = new StreamWriter(zip.CreateEntry($"assets/{modId}/lang/en.json").Open()))
            w.Write(Newtonsoft.Json.JsonConvert.SerializeObject(strings.Select((s, i) => (s, i)).ToDictionary(x => $"k{x.i}", x => x.s)));
        return path;
    }

    [Fact]
    public void Report_CountsErrorsPerMod_WithAnExample()
    {
        var log = GameLog.Parse(File.ReadAllText(Path.Combine(Data, "client-main-noise.log")));
        var report = ModErrorReport.Build(log, Mods(), DateTime.Now);

        var line = Assert.Single(report.Mods);
        Assert.Equal("crashtest", line.ModId);
        Assert.Equal("Crash Test", line.Name);
        Assert.True(line.Count >= 6);
        Assert.Contains("boom in a tick listener", line.Example);
        Assert.EndsWith("CrashTest_1.0.0.zip", line.Path); // по нему окно выключит мод
        Assert.True(report.IsNoisy == report.Total >= ModErrorReport.NoisyFrom);
    }

    [Fact]
    public void TranslationErrors_GoToTheModWhoseStringItIs_AndTheExampleShowsTheString()
    {
        var log = GameLog.Parse(File.ReadAllText(Path.Combine(Data, "client-main-translation.log")));
        var report = ModErrorReport.Build(log, Mods(LangMod("prospectingscan", "Configured radius: {0} | Configured height: {1}-{2}", "Enabled targets: {0}")), DateTime.Now);

        var line = Assert.Single(report.Mods);
        Assert.Equal(("prospectingscan", 2, CulpritSource.Translation), (line.ModId, line.Count, line.Source));
        Assert.Contains("Enabled targets: {0}", line.Example);
        Assert.Equal(0, report.Unattributed);
    }

    [Fact]
    public void Session_KeepsErrorsAcrossReads_EvenWhenAStackArrivesInTheNextChunk()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_root, "Logs")).FullName;
        var client = Path.Combine(logs, "client-main.log");
        var session = new GameSession(_root, DateTime.UtcNow.AddMinutes(-1));

        // запись ошибки пришла, а её стек — уже следующим чтением
        File.AppendAllLines(client, ["7.10.2026 00:21:09 [Error] Exception: CrashTest: boom"]);
        Assert.Equal(0, session.ErrorReport(() => Mods()).Total);
        File.AppendAllLines(client, ["   at CrashTestMod.Boom.Now(String where)", "7.10.2026 00:21:10 [Notification] next"]);

        var report = session.ErrorReport(() => Mods());
        Assert.Equal(1, report.Total);
        Assert.Equal("crashtest", Assert.Single(report.Mods).ModId); // стек дошёл — мод узнан
    }

    [Fact]
    public void Report_SurvivesARestartOfTheWindow()
    {
        var report = new ModErrorReport
        {
            At = new DateTime(2026, 10, 7, 1, 2, 3), Total = 42, Unattributed = 2, Dismissed = true,
            Mods = [new ModErrorLine("carryon", "Carry On", "1.0.0", @"C:\Mods\carryon.zip", 40, "boom", CulpritSource.Stack)],
        };
        report.Save(_profileId);

        var back = ModErrorReport.Load(_profileId)!;
        Assert.Equal(42, back.Total);
        Assert.True(back.Dismissed);
        Assert.Equal("carryon", back.Mods[0].ModId);
        Assert.Null(ModErrorReport.Load("нет-такого-профиля"));
    }
}

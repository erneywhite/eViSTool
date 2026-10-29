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
        if (File.Exists(ModErrorIgnores.FileFor(_profileId))) File.Delete(ModErrorIgnores.FileFor(_profileId));
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
        Assert.NotEmpty(line.Kinds!); // виды ошибок — для «Игнорировать»
    }

    // вид ошибки: имена в кавычках, числа и хеши не важны, текст — важен
    [Fact]
    public void Kind_IgnoresQuotedNamesAndNumbers()
    {
        var a = ModErrorKind.Of("[PlayerModelLib] Shape 'fseraph:entity/humanoid/seraph_l/clothing/collar' that replaces shape 'game:x' was not found, skipping.");
        var b = ModErrorKind.Of("[PlayerModelLib] Shape 'fseraph:entity/other' that replaces shape 'game:y/z'   was not found, skipping.");
        Assert.Equal(a, b);
        Assert.Equal("[PlayerModelLib] Shape '*' that replaces shape '*' was not found, skipping.", a);
        Assert.Equal(ModErrorKind.Of("Chunk at 512000, 110, 512064 not loaded for player 7f3a9c0d"),
            ModErrorKind.Of("Chunk at 3, 4, 5 not loaded for player 0b11e2ff"));
        Assert.NotEqual(a, ModErrorKind.Of("[PlayerModelLib] Model 'x' has no textures"));
    }

    // игнор: мод в игноре, пока все его виды знакомы; новый вид — снова в счёте; у старых агентов вид — по примеру
    [Fact]
    public void Ignores_HoldUntilANewKindAppears_AndSurviveARestart()
    {
        var known = new ModErrorLine("pml", "PML", "1.0", null, 4, "x", CulpritSource.Message, ["kind a"]);
        var report = new ModErrorReport { Total = 4 + 73, Unattributed = 73, UnattributedKinds = ["game a", "game b"], Mods = [known] };
        var ignores = new ModErrorIgnores();
        Assert.Equal(77, ignores.Active(report));

        ignores.Add("PML", known.KindList); // modid без учёта регистра
        ignores.Add(ModErrorIgnores.Game, report.UnattributedKindList);
        Assert.Equal(0, ignores.Active(report));
        Assert.Equal(0, ignores.ActiveMods(report));

        var newKind = known with { Kinds = ["kind a", "kind b"] };
        Assert.Equal(4, ignores.Active(report with { Mods = [newKind] }));
        Assert.Equal(73, ignores.Active(report with { UnattributedKinds = ["game c"] }));

        var oldAgent = new ModErrorLine("old", "Old", null, null, 2, "[old] boom at 15", CulpritSource.Message);
        ignores.Add("old", oldAgent.KindList);
        Assert.True(ignores.Has(oldAgent with { Example = "[old] boom at 99" }));

        ignores.Save(_profileId);
        var back = ModErrorIgnores.Load(_profileId);
        Assert.True(back.Has(known with { ModId = "pml" }));
        Assert.True(back.HasGame(report));
        back.Remove(ModErrorIgnores.Game);
        Assert.Equal(73, back.Active(report));

        Assert.True(ModErrorIgnores.Load("нет-такого-профиля").IsEmpty);
        new ModErrorIgnores().Save(_profileId); // пустой игнор — файла нет
        Assert.False(File.Exists(ModErrorIgnores.FileFor(_profileId)));
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
        // наблюдение за запуском игры — только у окна (Windows). На Linux у файла нет времени создания (.NET отдаёт
        // время последнего изменения), и LogTail принял бы каждую дописку лога за новый файл
        if (!OperatingSystem.IsWindows()) return;
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
            At = new DateTime(2026, 10, 7, 1, 2, 3), Total = 42, Unattributed = 2, UnattributedKinds = ["game"],
            Mods = [new ModErrorLine("carryon", "Carry On", "1.0.0", @"C:\Mods\carryon.zip", 40, "boom", CulpritSource.Stack, ["k1", "k2"])],
        };
        report.Save(_profileId);

        var back = ModErrorReport.Load(_profileId)!;
        Assert.Equal(42, back.Total);
        Assert.Equal(["game"], back.UnattributedKinds!);
        Assert.Equal("carryon", back.Mods[0].ModId);
        Assert.Equal(["k1", "k2"], back.Mods[0].Kinds!);
        Assert.Null(ModErrorReport.Load("нет-такого-профиля"));
    }
}

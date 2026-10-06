using System.IO.Compression;
using eViSTool.Core.Diagnostics;
using eViSTool.Core.Mods;

namespace eViSTool.Core.Tests;

/// <summary>
/// Разбор вылетов на настоящих логах со стенда: тестовый мод CrashTest ронял игру (ошибка в клиентской части),
/// закрывал мир (ошибка во встроенном сервере) и сыпал «тихими» ошибками. Плюс реальная ошибка перевода из лога игры.
/// </summary>
public sealed class CrashAnalyzerTests : IDisposable
{
    private static readonly string Data = Path.Combine(AppContext.BaseDirectory, "TestData", "Crash");
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-crash-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static IReadOnlyList<LogEntry> Log(string name) => GameLog.Parse(File.ReadAllText(Path.Combine(Data, name)));

    /// <summary>Установленные моды: тестовый CrashTest (настоящая DLL) и, по желанию, ещё моды.</summary>
    private ModFingerprints Mods(params string[] extra)
    {
        var list = new List<LocalMod>();
        var crash = Path.Combine(_root, "CrashTest_1.0.0.zip");
        File.Copy(Path.Combine(Data, "CrashTest_1.0.0.zip"), crash, overwrite: true);
        list.Add(ModScanner.ReadZip(crash));
        list.AddRange(extra.Select(ModScanner.ReadZip));
        return ModFingerprints.Build(list);
    }

    /// <summary>Мод с языковым файлом (как ProspectingScan) — для ошибок форматирования перевода.</summary>
    private string LangMod(string modId, params string[] strings)
    {
        var path = Path.Combine(_root, modId + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open()))
            w.Write($$"""{ "modid": "{{modId}}", "name": "{{modId}}", "version": "1.0.0" }""");
        using (var w = new StreamWriter(zip.CreateEntry($"assets/{modId}/lang/en.json").Open()))
            w.Write(Newtonsoft.Json.JsonConvert.SerializeObject(strings.Select((s, i) => (s, i)).ToDictionary(x => $"key{x.i}", x => x.s)));
        return path;
    }

    [Fact]
    public void Fingerprints_ReadTheNamespacesOfTheModsDll_AndNeverBlameTheGame()
    {
        var mods = Mods();
        Assert.Contains("CrashTestMod", mods.ByModId("crashtest")!.Namespaces);
        Assert.Equal("crashtest", mods.ByFrame("CrashTestMod.Boom.Now")!.ModId);
        Assert.Null(mods.ByFrame("Vintagestory.Common.EventManager.TriggerGameTick"));
        Assert.Null(mods.ByFrame("System.String.Format"));
    }

    [Fact]
    public void GameCrash_TheGameNamesTheMod_InItsCrashReport()
    {
        var finding = CrashAnalyzer.Analyze(Log("client-main-crash.log"), File.ReadAllText(Path.Combine(Data, "client-crash.log")), null, Mods())!;

        Assert.Equal(GameExitKind.Crashed, finding.Kind);
        Assert.Equal("crashtest", finding.Culprit!.ModId);
        Assert.Equal("1.0.0", finding.Culprit.Version);
        Assert.Equal(CulpritSource.GameReport, finding.Culprit.Source);
        Assert.NotNull(finding.Culprit.Mod); // установлен — можно выключить или откатить
        Assert.Contains("boom in a world callback", finding.Error);
        Assert.Contains("CrashTestMod.Boom.Now", finding.Stack);
    }

    [Fact]
    public void GameCrash_WithoutTheReportFile_IsReadFromTheFatalLogEntry()
    {
        var finding = CrashAnalyzer.Analyze(Log("client-main-crash.log"), null, null, Mods())!;
        Assert.Equal(GameExitKind.Crashed, finding.Kind);
        Assert.Equal("crashtest", finding.Culprit!.ModId);
    }

    [Fact]
    public void GameCrash_NamingAModThatIsNotInstalled_StillNamesIt()
    {
        var finding = CrashAnalyzer.Analyze([], "Critical error occurred in the following mod: ghostmod@2.1.0\nSystem.Exception: x", null,
            ModFingerprints.Build([]))!;
        Assert.Equal("ghostmod", finding.Culprit!.ModId);
        Assert.Equal("2.1.0", finding.Culprit.Version);
        Assert.Null(finding.Culprit.Mod);
    }

    [Fact]
    public void WorldClosed_ByTheBuiltInServer_TheModIsFoundByTheStackInTheServerLog()
    {
        var finding = CrashAnalyzer.Analyze(Log("client-main-disconnected.log"), null, Log("server-main-disconnected.log"), Mods())!;

        Assert.Equal(GameExitKind.WorldClosed, finding.Kind);
        Assert.Contains("Too many errors", finding.Reason);
        Assert.Equal("crashtest", finding.Culprit!.ModId);
        Assert.Equal(CulpritSource.Stack, finding.Culprit.Source);
        Assert.True(finding.ErrorCount > 1);
        Assert.Contains("boom in a server callback", finding.Error);
    }

    [Fact]
    public void QuietErrors_AreNotACrash_ButEachIsAttributedToItsMod()
    {
        var log = Log("client-main-noise.log");
        Assert.Null(CrashAnalyzer.Analyze(log, null, null, Mods()));

        // последняя ошибка в образце обрезана вместе со стеком — берём те, у которых он есть
        var errors = CrashAnalyzer.Errors(log).Where(e => e.Entry.StackFrames.Any()).ToList();
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.Equal("crashtest", CrashAnalyzer.Attribute(e, Mods())!.ModId));
    }

    [Fact]
    public void TranslationError_WithOnlyGameCodeInTheStack_IsFoundByTheModsLangFile()
    {
        var mods = Mods(LangMod("prospectingscan", "Configured radius: {0} | Configured height: {1}-{2}", "Enabled targets: {0}"));

        var errors = CrashAnalyzer.Errors(Log("client-main-translation.log")).ToList();

        Assert.Equal(2, errors.Count);
        Assert.All(errors, e =>
        {
            var culprit = CrashAnalyzer.Attribute(e, mods)!;
            Assert.Equal("prospectingscan", culprit.ModId);
            Assert.Equal(CulpritSource.Translation, culprit.Source);
        });
    }

    [Fact]
    public void NormalLeave_IsNotAFinding()
    {
        var log = GameLog.Parse("""
            7.10.2026 00:15:00 [Notification] Exiting current game to main menu, reason: leave world button pressed
            7.10.2026 00:15:01 [Notification] Exiting game now. Server running=False. Exit reason: Main screen quit button was pressed
            """);
        Assert.Null(CrashAnalyzer.Analyze(log, null, null, ModFingerprints.Build([])));
    }

    [Fact]
    public void Log_InEnglishDateFormat_IsParsedToo()
    {
        var log = GameLog.Parse("""
            10/7/2026 12:18:55 AM [Error] Exception: boom
               at CrashTestMod.Boom.Now(String where)
            10/7/2026 12:18:56 AM [Notification] next
            """);
        Assert.Equal(2, log.Count);
        Assert.Equal(["CrashTestMod.Boom.Now"], log[0].StackFrames);
    }

    [Theory]
    [InlineData("[crashtest] An exception was thrown when trying to start the mod:")]   // modid
    [InlineData("[Crash Test] something went wrong")]                                     // имя мода
    [InlineData("[CrashTestSystem][OnBlockInteractStart] unsupported block type")]      // имя его класса
    [InlineData("Failed to run mod phase Start for mod CrashTestMod.Core")]              // сборка внутри его пространства имён
    public void ModTagInTheMessage_NamesTheMod(string message)
    {
        var error = new CrashAnalyzer.ErrorEntry(new LogEntry("7.10.2026 00:00:00", "Error", message, []), null);
        var culprit = CrashAnalyzer.Attribute(error, Mods())!;
        Assert.Equal("crashtest", culprit.ModId);
        Assert.Equal(CulpritSource.Message, culprit.Source);
    }

    [Fact]
    public void GameMessages_WithoutAModTag_AreNotBlamedOnAMod()
    {
        var error = new CrashAnalyzer.ErrorEntry(new LogEntry("7.10.2026 00:00:00", "Error",
            "The dialog GuiDialogBlockEntityRecipeSelector requested focus, but was not added yet.", []), null);
        Assert.Null(CrashAnalyzer.Attribute(error, Mods()));
    }
}

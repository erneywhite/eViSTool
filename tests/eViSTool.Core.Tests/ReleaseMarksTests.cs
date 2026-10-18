using System.IO.Compression;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Versioning;
using Newtonsoft.Json;

namespace eViSTool.Core.Tests;

/// <summary>
/// Версия в modinfo.json скачанного мода не та, что у релиза на ModDB (автор забыл поднять): сравнение версий,
/// предупреждение, отметка «файл релиза» и проверка обновлений с ней — тот же релиз не предлагается снова.
/// Файлы отметок — во временной папке теста; в общей папке данных — только под id профилей теста, потом убираются.
/// </summary>
public sealed class ReleaseMarksTests : IDisposable
{
    private static readonly ModVersion Game = ModVersion.ParseOrNull("1.21.6")!;

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-marks-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _id = "marks-" + Guid.NewGuid().ToString("N")[..8];

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        var backups = Path.Combine(AppPaths.ModBackups, _id);
        if (Directory.Exists(backups)) Directory.Delete(backups, recursive: true);
        if (File.Exists(ModHistory.FileFor(_id))) File.Delete(ModHistory.FileFor(_id));
        if (File.Exists(ReleaseMarks.FileFor(_id))) File.Delete(ReleaseMarks.FileFor(_id));
    }

    private string MarksFile => Path.Combine(_root, "marks.json");

    private static ModDbRelease Rel(string version, long id = 7, string tag = "1.21.0") =>
        new() { ReleaseId = id, ModVersion = version, Tags = [tag], MainFile = $"https://moddbcdn.vintagestory.at/carryon_{version}.zip" };

    private static ModInfo Info(string? version) => new() { ModId = "carryon", OriginalModId = "carryon", Name = "Carry On", Version = version };

    private static LocalMod Local(string fileName, string? version, ReleaseMark? mark = null) =>
        new($"C:/VS/Mods/{fileName}", Info(version), null) { Release = mark };

    private static Dictionary<string, ModDbResult> Remote(params ModDbRelease[] releases) =>
        new(StringComparer.OrdinalIgnoreCase) { ["carryon"] = new(new ModDbMod { Name = "Carry On", Releases = [.. releases] }, null) };

    // ---- сравнение версий

    [Theory]
    [InlineData("1.8.0", "1.8.0", false)]
    [InlineData("v1.8.0", "1.8.0", false)]   // «v» — та же версия
    [InlineData("1.8.0", "V1.8.0", false)]
    [InlineData("1.8", "1.8.0", false)]      // недостающие части — нули
    [InlineData(" 1.8.0 ", "1.8.0", false)]
    [InlineData("1.8.0", "1.8.1", true)]
    [InlineData("1.8.2", "1.8.1", true)]     // в файле новее — тоже расхождение
    [InlineData("1.8.1-rc.1", "1.8.1-rc.1", false)]
    [InlineData("1.8.1-rc.1", "v1.8.1-rc.1", false)]
    [InlineData("1.8.1-rc.1", "1.8.1", true)] // пре-релиз и выпуск — разные версии
    [InlineData("1.8.1-rc.1", "1.8.1-rc.2", true)]
    [InlineData("beta", "Beta", false)]      // не разбирается — сравнение текста без регистра
    [InlineData("beta", "gamma", true)]
    [InlineData(null, "1.8.1", true)]        // в файле версии нет
    [InlineData("", "1.8.1", true)]
    [InlineData("1.8.0", null, false)]       // у релиза версии нет — сравнивать не с чем
    [InlineData("1.8.0", " ", false)]
    public void Differs_ByMeaning(string? file, string? release, bool differs) =>
        Assert.Equal(differs, ReleaseMarks.Differs(file, release));

    [Fact]
    public void Warning_OnlyWhenVersionsDiffer()
    {
        Assert.Null(ReleaseMarks.Warning("Carry On", "1.8.0", "1.8.0"));
        Assert.Null(ReleaseMarks.Warning("Carry On", "v1.8.0", "1.8.0"));
        Assert.Equal(Loc.T("dl.versionMismatch", "Carry On", "1.8.0", "1.8.1"), ReleaseMarks.Warning("Carry On", "1.8.0", "1.8.1"));
        Assert.Equal(Loc.T("dl.versionMissing", "Carry On", "1.8.1"), ReleaseMarks.Warning("Carry On", null, "1.8.1"));
    }

    // ---- отметки

    [Fact]
    public void Update_Mismatch_MarksTheFile_ThenOtherInstallClearsIt()
    {
        ReleaseMarks.Update(MarksFile, Info("1.8.0"), "carryon_1.8.1.zip", Rel("1.8.1", id: 42));

        var marks = ReleaseMarks.Load(MarksFile);
        Assert.Equal(new ReleaseMark("carryon_1.8.1.zip", "1.8.0", 42, "1.8.1"), marks["carryon"]);
        Assert.Equal(marks["carryon"], marks["CarryOn"]); // ключ — modid без учёта регистра

        // поставили что-то не с ModDB (копию из хранилища, свой архив) — отметка снята
        ReleaseMarks.Update(MarksFile, Info("1.8.0"), "carryon_1.8.0.zip", release: null);
        Assert.Empty(ReleaseMarks.Load(MarksFile));
    }

    [Fact]
    public void Update_SameVersions_NoFileCreated_AndOldMarkCleared()
    {
        ReleaseMarks.Update(MarksFile, Info("1.8.0"), "carryon_1.8.0.zip", Rel("v1.8.0"));
        Assert.False(File.Exists(MarksFile)); // обычные установки файла отметок не создают

        ReleaseMarks.Update(MarksFile, Info("1.8.0"), "carryon_1.8.1.zip", Rel("1.8.1"));
        ReleaseMarks.Update(MarksFile, Info("1.8.2"), "carryon_1.8.2.zip", Rel("1.8.2")); // автор исправился
        Assert.Empty(ReleaseMarks.Load(MarksFile));
    }

    [Fact]
    public void Match_OnlyTheSameFile()
    {
        ReleaseMarks.Update(MarksFile, Info("1.8.0"), "carryon_1.8.1.zip", Rel("1.8.1"));
        var marks = ReleaseMarks.Load(MarksFile);

        Assert.NotNull(ReleaseMarks.Match(marks, Local("carryon_1.8.1.zip", "1.8.0")));
        Assert.NotNull(ReleaseMarks.Match(marks, Local("CarryOn_1.8.1.ZIP", "1.8.0")));
        // путь удалённого сервера любой машины: имя файла — после последней косой черты любого вида
        Assert.NotNull(ReleaseMarks.Match(marks, new LocalMod(@"C:\VS\Mods\carryon_1.8.1.zip", Info("1.8.0"), null)));
        Assert.NotNull(ReleaseMarks.Match(marks, new LocalMod("/srv/vs/Mods/carryon_1.8.1.zip", Info("1.8.0"), null)));
        // файл заменили: другое имя (настоящий 1.8.0, положенный руками) или другая версия внутри — отметка не про него
        Assert.Null(ReleaseMarks.Match(marks, Local("carryon_1.8.0.zip", "1.8.0")));
        Assert.Null(ReleaseMarks.Match(marks, Local("carryon_1.8.1.zip", "1.8.1")));
        Assert.Null(ReleaseMarks.Match(marks, new LocalMod("C:/VS/Mods/carryon_1.8.1.zip", null, "битый")));
    }

    [Fact]
    public void Load_BrokenOrMissingFile_IsEmpty()
    {
        Assert.Empty(ReleaseMarks.Load(MarksFile));
        File.WriteAllText(MarksFile, "{ не json");
        Assert.Empty(ReleaseMarks.Load(MarksFile));
        File.WriteAllText(MarksFile, """{ "carryon": { "FileName": "", "ReleaseVersion": "1.8.1" }, "other": null }""");
        Assert.Empty(ReleaseMarks.Load(MarksFile));
    }

    // ---- проверка обновлений

    [Fact]
    public void Evaluate_WithoutMark_OffersTheSameReleaseAgain()
    {
        // так было до отметок: в файле 1.8.0, на ModDB 1.8.1 — «есть обновление» после каждой установки
        var r = UpdateChecker.Evaluate([Local("carryon_1.8.1.zip", "1.8.0")], Remote(Rel("1.8.1")), Game).Single();
        Assert.Equal(ModStatus.UpdateAvailable, r.Status);
    }

    [Fact]
    public void Evaluate_MarkedFile_UpToDate_WithNote()
    {
        var mark = new ReleaseMark("carryon_1.8.1.zip", "1.8.0", 7, "1.8.1");
        var r = UpdateChecker.Evaluate([Local("carryon_1.8.1.zip", "1.8.0", mark)], Remote(Rel("1.8.0", id: 6), Rel("1.8.1")), Game).Single();

        Assert.Equal(ModStatus.UpToDate, r.Status);
        Assert.Equal(Loc.T("status.releaseFile", "1.8.1"), r.Message);
    }

    [Fact]
    public void Evaluate_MarkedFile_NewerReleaseStillOffered()
    {
        var mark = new ReleaseMark("carryon_1.8.1.zip", "1.8.0", 7, "1.8.1");
        var r = UpdateChecker.Evaluate([Local("carryon_1.8.1.zip", "1.8.0", mark)], Remote(Rel("1.8.1"), Rel("1.8.2", id: 8)), Game).Single();

        Assert.Equal(ModStatus.UpdateAvailable, r.Status);
        Assert.Equal("1.8.2", r.LatestCompatible!.ModVersion);
    }

    [Fact]
    public void Evaluate_FileSaysNewerThanRelease_RealNewerReleaseOffered()
    {
        // в файле релиза 1.8.1 автор написал 1.8.2; без отметки настоящий 1.8.2 сочли бы уже стоящим
        var mark = new ReleaseMark("carryon_1.8.1.zip", "1.8.2", 7, "1.8.1");
        var r = UpdateChecker.Evaluate([Local("carryon_1.8.1.zip", "1.8.2", mark)], Remote(Rel("1.8.1"), Rel("1.8.2", id: 8)), Game).Single();

        Assert.Equal(ModStatus.UpdateAvailable, r.Status);
        Assert.Equal("1.8.2", r.LatestCompatible!.ModVersion);
    }

    [Fact]
    public void Evaluate_MarkedPrerelease_KeepsUnstableBranch()
    {
        // файл — пре-релиз 1.9.0-rc.1, внутри написано 1.8.1: человек на нестабильной ветке, предлагается следующий rc
        var mark = new ReleaseMark("carryon_1.9.0-rc.1.zip", "1.8.1", 7, "1.9.0-rc.1");
        var local = Local("carryon_1.9.0-rc.1.zip", "1.8.1", mark);

        var same = UpdateChecker.Evaluate([local], Remote(Rel("1.8.1", id: 5), Rel("1.9.0-rc.1")), Game).Single();
        Assert.Equal(ModStatus.UpToDate, same.Status);

        var next = UpdateChecker.Evaluate([local], Remote(Rel("1.8.1", id: 5), Rel("1.9.0-rc.1"), Rel("1.9.0-rc.2", id: 8)), Game).Single();
        Assert.Equal(ModStatus.UpdateAvailable, next.Status);
        Assert.Equal("1.9.0-rc.2", next.LatestCompatible!.ModVersion);
    }

    // ---- установка в свой профиль: отметка ставится и подхватывается сканом

    private (ModTarget Target, string Mods) LocalServer()
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, "server")).FullName;
        var mods = Directory.CreateDirectory(Path.Combine(data, "Mods")).FullName;
        File.WriteAllText(Path.Combine(data, "serverconfig.json"), JsonConvert.SerializeObject(new { ModPaths = new[] { "Mods", mods } }));
        return (ModTarget.For(new GameProfile { Id = _id, Name = "Сервер", Kind = ProfileKind.Server, DataDir = data })!, mods);
    }

    private string Zip(string fileName, string version)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "dl", Guid.NewGuid().ToString("N")[..6])).FullName;
        var path = Path.Combine(dir, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open());
        w.Write($$"""{ "modid": "carryon", "name": "Carry On", "version": "{{version}}" }""");
        return path;
    }

    [Fact]
    public async Task InstallRelease_Mismatch_Installed_AndNotOfferedAgain()
    {
        var (target, mods) = LocalServer();
        var release = Rel("1.8.1");

        var outcome = await ModTargets.InstallAsync(target, Zip("carryon_1.8.1.zip", "1.8.0"), release: release);

        Assert.Equal("Carry On 1.8.0", outcome!.Text); // установка прошла
        Assert.True(File.Exists(Path.Combine(mods, "carryon_1.8.1.zip")));
        var (_, scanned) = await ModTargets.ScanAsync(target);
        var mod = Assert.Single(scanned);
        Assert.Equal("1.8.1", mod.Release?.ReleaseVersion);
        Assert.Equal(ModStatus.UpToDate, UpdateChecker.Evaluate(scanned, Remote(Rel("1.8.0", id: 6), release), Game).Single().Status);
    }

    [Fact]
    public async Task InstallRelease_SameVersion_NoMark()
    {
        var (target, _) = LocalServer();

        await ModTargets.InstallAsync(target, Zip("carryon_1.8.1.zip", "v1.8.1"), release: Rel("1.8.1"));

        var (_, scanned) = await ModTargets.ScanAsync(target);
        Assert.Null(Assert.Single(scanned).Release);
        Assert.False(File.Exists(ReleaseMarks.FileFor(_id)));
    }

    [Fact]
    public async Task InstallOtherZip_ClearsMark()
    {
        var (target, _) = LocalServer();
        await ModTargets.InstallAsync(target, Zip("carryon_1.8.1.zip", "1.8.0"), release: Rel("1.8.1"));

        // откат на копию с тем же именем и той же версией внутри — это уже не файл релиза 1.8.1
        await ModTargets.InstallAsync(target, Zip("carryon_1.8.1.zip", "1.8.0"));

        var (_, scanned) = await ModTargets.ScanAsync(target);
        Assert.Null(Assert.Single(scanned).Release);
        Assert.Equal(ModStatus.UpdateAvailable, UpdateChecker.Evaluate(scanned, Remote(Rel("1.8.1")), Game).Single().Status);
    }

    // ---- агент: заголовок релиза, отметка в списке модов

    [Fact]
    public void ReleaseHeader_RoundTrip_AndGarbageIgnored()
    {
        var value = AgentClient.ReleaseHeaderValue(Rel("1.8.1-rc.1+build;7", id: 1234))!;
        Assert.DoesNotContain(' ', value);
        var back = AgentClient.ParseReleaseHeader(value)!;
        Assert.Equal(1234, back.ReleaseId);
        Assert.Equal("1.8.1-rc.1+build;7", back.ModVersion);

        Assert.Null(AgentClient.ReleaseHeaderValue(null));
        Assert.Null(AgentClient.ReleaseHeaderValue(new ModDbRelease { ReleaseId = 1, ModVersion = null }));
        Assert.Null(AgentClient.ParseReleaseHeader(null));
        Assert.Null(AgentClient.ParseReleaseHeader(""));
        Assert.Null(AgentClient.ParseReleaseHeader("abc;1.0"));
        Assert.Null(AgentClient.ParseReleaseHeader("-5;1.0"));
        Assert.Null(AgentClient.ParseReleaseHeader("5;"));
        Assert.Null(AgentClient.ParseReleaseHeader(";1.0"));
        Assert.Null(AgentClient.ParseReleaseHeader("5;" + new string('1', 400)));
    }

    [Fact]
    public void RemoteModList_CarriesMark_OldAgentWithout()
    {
        var mark = new ReleaseMark("carryon_1.8.1.zip", "1.8.0", 7, "1.8.1");
        var json = JsonConvert.SerializeObject(new RemoteModList("1.21.6", [], ["/srv/Mods"], "/srv/Mods",
            [new RemoteMod("/srv/Mods/carryon_1.8.1.zip", Info("1.8.0"), null, mark)], null));

        var (_, mods) = ModTargets.Remote(new GameProfile { Id = "remote" }, JsonConvert.DeserializeObject<RemoteModList>(json)!);
        Assert.Equal(mark, Assert.Single(mods).Release);

        // агент до 0.14.1 поля не присылает — отметки нет, сравнение по modinfo, как раньше
        var old = """{ "GameVersion": "1.21.6", "DisabledMods": [], "ModDirs": [], "Mods": [ { "Path": "/srv/Mods/x.zip", "Info": { "ModId": "x" } } ] }""";
        var (_, oldMods) = ModTargets.Remote(new GameProfile { Id = "remote" }, JsonConvert.DeserializeObject<RemoteModList>(old)!);
        Assert.Null(Assert.Single(oldMods).Release);
    }
}

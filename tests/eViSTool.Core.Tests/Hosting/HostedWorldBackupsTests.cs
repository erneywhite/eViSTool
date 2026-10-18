using System.IO.Compression;
using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// Копии мира сервера у хостера — на поддельном FTP «как G-Portal» (поверх не переименовывает): где мир, откуда качать,
/// докачка, «мир менялся», отмена, архив настроек, ротация и восстановление со сбоем на каждом шаге.
/// </summary>
public sealed class HostedWorldBackupsTests : IDisposable
{
    private const string World = "/Saves/MyVintageStoryWorld.vcdbs";
    private const string Tmp = World + ".evistool.tmp";
    private const string Bak = World + ".evistool.bak";

    private readonly HostedFixture _host = new();
    private readonly string _copies;
    private readonly FakeTime _time = new(new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc));

    public HostedWorldBackupsTests()
    {
        _host.GPortalLayout();
        _copies = Path.Combine(_host.Dir, "copies");
        _host.Settings.BackupDir = _copies;
    }

    public void Dispose() => _host.Dispose();

    private HostedWorldBackups Backups() => new(_host.Target) { Time = _time, FreeSpace = _ => null };

    private string ServerLocal(string remote) => Path.Combine([_host.Server.Root, .. remote.Split('/', StringSplitOptions.RemoveEmptyEntries)]);

    /// <summary>Настоящий мир SQLite на «сервере» (прежний файл заменяется).</summary>
    private void PutWorldAt(string remote, params string[] rows)
    {
        var local = ServerLocal(remote);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        foreach (var f in new[] { local, local + "-wal", local + "-shm" }) File.Delete(f);
        SqliteWorld.WriteClean(local, rows);
    }

    /// <summary>Мир сервера (/Saves/MyVintageStoryWorld.vcdbs) с этими строками.</summary>
    private void PutWorld(params string[] rows) => PutWorldAt(World, rows);

    /// <summary>Строки, которые крупнее кусков передачи: мир на полмегабайта.</summary>
    private static string[] BigRows(string tag) => [.. Enumerable.Range(0, 60).Select(i => tag + i + new string('x', 8000))];

    private string[] ServerRows(string remote = World) => SqliteWorld.Read(ServerLocal(remote));

    private string[] CopyFiles() => Directory.Exists(_copies) ? [.. Directory.GetFiles(_copies).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)] : [];

    private void SetTime(string remote, DateTime utc) => File.SetLastWriteTimeUtc(ServerLocal(remote), utc);

    // ---------- где мир

    [Fact]
    public async Task Locate_ByConfig_WithDoubledSlashes()
    {
        PutWorld("a");

        var world = await Backups().LocateAsync(default);

        Assert.Equal(World, world.Path);
        Assert.Equal(HostedWorldFoundBy.Config, world.FoundBy);
        Assert.Equal("/home/sid_1234567/vintageStory/Saves/MyVintageStoryWorld.vcdbs", world.ServerPath);
        Assert.True(world.Exists);
        Assert.Equal("/", world.DataDir);
    }

    [Fact]
    public async Task Locate_WindowsPathOfTheTemplate_IsSkipped_TheLogNamesTheWorld()
    {
        // шаблон хостера до первого запуска: путь Windows с их машины; в Saves два мира — по Saves не понять какой
        _host.Server.Put("/serverconfig.json", File.ReadAllBytes(HostedFixture.Sample("gportal-serverconfig-template.json")));
        PutWorld("a");
        PutWorldAt("/Saves/default.vcdbs", "b");

        var world = await Backups().LocateAsync(default);

        Assert.Equal(HostedWorldFoundBy.Log, world.FoundBy);
        Assert.Equal(World, world.Path);
        Assert.True(world.Exists);
    }

    [Fact]
    public async Task Locate_RealLogOfGPortal_NamesTheWorld()
    {
        _host.Server.Put("/serverconfig.json", "{}");
        _host.Server.Put("/Logs/server-main.log", File.ReadAllBytes(HostedFixture.Sample(Path.Combine("Hosting", "gportal-server-main.log"))));
        PutWorld("a");

        var world = await Backups().LocateAsync(default);

        Assert.Equal((HostedWorldFoundBy.Log, World), (world.FoundBy, world.Path));
    }

    [Fact]
    public async Task Locate_NoConfigNoLog_TakesTheOnlyWorldInSaves()
    {
        using var host = new HostedFixture();
        host.GPortalLayout(config: false, log: false);
        host.Settings.BackupDir = Path.Combine(host.Dir, "copies");
        host.Server.Put("/Saves/Other.vcdbs", [1]);
        host.Server.Put("/Saves/Other.vcdbs-wal", [1]); // журнал — не мир
        host.Server.Put("/Saves/XLeveling/x.json", "{}");

        var world = await new HostedWorldBackups(host.Target).LocateAsync(default);

        Assert.Equal((HostedWorldFoundBy.Saves, "/Saves/Other.vcdbs", (string?)null), (world.FoundBy, world.Path, world.ServerPath));
    }

    [Fact]
    public async Task Locate_SeveralWorldsInSaves_OrNone_SaysSoInItsOwnWords()
    {
        using var host = new HostedFixture();
        host.GPortalLayout(config: false, log: false);
        var backups = new HostedWorldBackups(host.Target);

        var none = await Assert.ThrowsAsync<HostedWorldException>(() => backups.LocateAsync(default));
        Assert.Equal(HostedWorldProblem.NotFound, none.Problem);
        HostedTestText.AssertIs("hosted.worldNotFound", none.Message);

        host.Server.Put("/Saves/a.vcdbs", [1]);
        host.Server.Put("/Saves/b.vcdbs", [1]);
        var many = await Assert.ThrowsAsync<HostedWorldException>(() => backups.LocateAsync(default));
        Assert.Equal(HostedWorldProblem.Ambiguous, many.Problem);
        Assert.Contains("a.vcdbs, b.vcdbs", many.Message);
    }

    [Fact]
    public async Task Locate_PathKnownButNoFile_IsMissing_AndTheCopyIsRefused()
    {
        _host.Server.MakeDir("/Saves");

        var world = await Backups().LocateAsync(default);
        Assert.False(world.Exists);
        Assert.Equal(World, world.Path);

        var ex = await Assert.ThrowsAsync<HostedWorldException>(() => Backups().CreateAsync(serverStopped: true, null, default));
        Assert.Equal(HostedWorldProblem.Missing, ex.Problem);
        Assert.Contains("/home/sid_1234567/vintageStory/Saves/MyVintageStoryWorld.vcdbs", ex.Message);
    }

    // ---------- откуда качать

    [Fact]
    public async Task Source_StoppedServer_TakesTheWorld_RunningServer_TheFreshestGameCopy()
    {
        PutWorld("live");
        PutWorldAt("/Backups/old.vcdbs", "old");
        PutWorldAt("/BackupSaves/fresh.vcdbs", "fresh");
        PutWorldAt("/Backups/older.vcdbs", "older");
        SetTime("/Backups/old.vcdbs", _time.UtcNow.AddHours(-2));
        SetTime("/BackupSaves/fresh.vcdbs", _time.UtcNow.AddHours(-1));
        SetTime("/Backups/older.vcdbs", _time.UtcNow.AddDays(-3));

        var stopped = await Backups().SourceAsync(serverStopped: true, default);
        Assert.Equal((HostedWorldOrigin.World, World), (stopped.Origin, stopped.File.Path));

        var running = await Backups().SourceAsync(serverStopped: false, default);
        Assert.Equal((HostedWorldOrigin.GameBackup, "/BackupSaves/fresh.vcdbs"), (running.Origin, running.File.Path));
        Assert.Equal(World, running.World?.Path);
    }

    [Fact]
    public async Task Source_RunningServerWithoutGameCopies_IsRefused_NothingIsSaved()
    {
        PutWorld("live");
        _host.Server.MakeDir("/Backups");

        var ex = await Assert.ThrowsAsync<HostedWorldException>(() => Backups().CreateAsync(serverStopped: false, null, default));

        Assert.Equal(HostedWorldProblem.NeedsStop, ex.Problem);
        HostedTestText.AssertIs("hosted.worldNeedsStop", ex.Message);
        Assert.Empty(CopyFiles());
        Assert.DoesNotContain(_host.Server.DownloadOffsets, d => d.Path == World); // работающий мир не качали
    }

    /// <summary>Текст — один из переводов ключа, где на месте {0} что угодно.</summary>
    private static void AssertLike(string key, string actual) =>
        Assert.Contains(HostedTestText.Of(key), t => actual.StartsWith(t[..t.IndexOf("{0}", StringComparison.Ordinal)], StringComparison.Ordinal));

    // ---------- копия

    [Fact]
    public async Task Create_StoppedServer_SavesTheWorld_WithExtraArchive_Info_AndProgress()
    {
        PutWorld("one", "two");
        _host.Server.Put("/ModConfig/carryon.json", """{ "a": 1 }""");
        _host.Server.Put("/ModConfig/sub/deep.json", """{ "b": 2 }""");
        _host.Server.Put("/ModConfig/carryon.json.evistool.bak", "наш bak — не данные");
        _host.Server.Put("/Playerdata/playerdata.json", "[]");
        _host.Server.Put("/ModData/xskills/data.bin", [1, 2, 3]);
        _host.Server.Put("/Saves/XLeveling/progress.json", "{}");
        _host.Server.Put("/Backups/genbackup.vcdbs", [9]);
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        await ModTargets.ScanAsync(_host.Target); // моды последнего скана — в mods.json
        var steps = new List<HostedTransfer>();

        var result = await Backups().CreateAsync(serverStopped: true, new SyncProgress<HostedTransfer>(steps.Add), default);

        var copy = result.Backup;
        var expectedName = "Hosted-" + _time.GetLocalNow().DateTime.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture) + ".vcdbs";
        Assert.Equal(expectedName, copy.Name);
        Assert.Equal(["one", "two"], SqliteWorld.Read(copy.Path));
        Assert.True(copy.IsOwn);
        Assert.Equal(HostedWorldOrigin.World, copy.Origin);
        Assert.Equal(World, copy.SourcePath);
        Assert.True(copy.HasExtra);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Pruned);
        Assert.Equal([expectedName[..^6] + ".extra.zip", expectedName[..^6] + ".info.json", expectedName], CopyFiles());

        using (var zip = ZipFile.OpenRead(HostedWorldBackups.ExtraFor(copy.Path)))
        {
            Assert.Equal(
                ["ModConfig/carryon.json", "ModConfig/sub/deep.json", "ModData/xskills/data.bin", "Playerdata/playerdata.json",
                    "Saves/XLeveling/progress.json", "mods.json", "serverconfig.json"],
                zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
            using var reader = new StreamReader(zip.GetEntry("mods.json")!.Open());
            var mods = JObject.Parse(reader.ReadToEnd())["mods"]!.ToObject<JObject[]>()!;
            var alpha = mods.Single(m => (string?)m["modid"] == "alpha");
            Assert.Equal(("1.0.0", "alpha_1.0.0.zip"), ((string?)alpha["version"], (string?)alpha["file"]));
        }

        var world = steps.Where(s => s.Step == HostedBackupStep.World).ToList();
        Assert.NotEmpty(world);
        Assert.Equal(new FileInfo(ServerLocal(World)).Length, world[^1].Total);
        Assert.Equal(world[^1].Total, world[^1].Done);
        Assert.Contains(steps, s => s.Step == HostedBackupStep.Extra);
    }

    [Fact]
    public async Task Create_RunningServer_TakesTheGameCopy_AndListsItsOrigin()
    {
        PutWorld("live");
        PutWorldAt("/Backups/genbackup.vcdbs", "from-genbackup");

        var result = await Backups().CreateAsync(serverStopped: false, null, default);

        Assert.Equal(["from-genbackup"], SqliteWorld.Read(result.Backup.Path));
        var listed = Assert.Single(Backups().List());
        Assert.Equal((HostedWorldOrigin.GameBackup, "/Backups/genbackup.vcdbs"), (listed.Origin, listed.SourcePath));
        Assert.Equal(result.Backup.Size, listed.Size);
        Assert.True(listed.ExtraSize > 0);
    }

    [Fact]
    public async Task Create_CrashedServerWithJournal_MergesTheJournalIntoTheCopy()
    {
        SqliteWorld.WriteCrashed(ServerLocal(World), ["saved"], ["only-in-wal"]);

        var result = await Backups().CreateAsync(serverStopped: true, null, default);

        Assert.Equal(["saved", "only-in-wal"], SqliteWorld.Read(result.Backup.Path));
        Assert.False(File.Exists(result.Backup.Path + "-wal")); // копия — один цельный файл
        Assert.DoesNotContain(CopyFiles(), f => f.Contains(".part"));
    }

    [Fact]
    public async Task Create_ResumesAfterDrops_FromWhereItStopped()
    {
        PutWorld(BigRows("r"));
        _host.Server.CutDownloads.Add((World, 100_000));
        _host.Server.CutDownloads.Add((World, 50_000));
        var connects = _host.Server.Connects;

        var result = await Backups().CreateAsync(serverStopped: true, null, default);

        Assert.Equal(BigRows("r"), SqliteWorld.Read(result.Backup.Path));
        Assert.Equal([0L, 100_000L, 150_000L], _host.Server.DownloadOffsets.Where(d => d.Path == World).Select(d => d.Offset));
        Assert.True(_host.Server.Connects - connects >= 3); // каждый обрыв — новое соединение
    }

    [Fact]
    public async Task Create_ServerWithoutRangeReads_StartsOverAfterADrop()
    {
        PutWorld(BigRows("r"));
        _host.Server.NoRange = true;
        _host.Server.CutDownloads.Add((World, 100_000));

        var result = await Backups().CreateAsync(serverStopped: true, null, default);

        Assert.Equal(BigRows("r"), SqliteWorld.Read(result.Backup.Path));
        Assert.Equal([0L, 0L], _host.Server.DownloadOffsets.Where(d => d.Path == World).Select(d => d.Offset));
    }

    [Fact]
    public async Task Create_ServerWithoutRangeReads_CutEveryTime_GivesUp_NoPart()
    {
        PutWorld(BigRows("r"));
        _host.Server.NoRange = true;
        for (var i = 0; i < 10; i++) _host.Server.CutDownloads.Add((World, 100_000));

        await Assert.ThrowsAsync<RemoteFilesException>(() => Backups().CreateAsync(serverStopped: true, null, default));

        Assert.Empty(CopyFiles());
        Assert.True(_host.Server.DownloadOffsets.Count(d => d.Path == World) <= 10);
    }

    [Fact]
    public async Task Create_WorldGrowsDuringTheDownload_NoCopy_NoPart()
    {
        PutWorld(BigRows("r"));
        var once = true;
        _host.Server.OnBefore = (op, path) =>
        {
            if (op != "download" || path != World || !once) return;
            once = false;
            File.AppendAllText(ServerLocal(World), new string('z', 5000)); // сервер пишет мир прямо сейчас
        };

        var ex = await Assert.ThrowsAsync<HostedWorldException>(() => Backups().CreateAsync(serverStopped: true, null, default));

        Assert.Equal(HostedWorldProblem.Changed, ex.Problem);
        HostedTestText.AssertIs("hosted.worldChanged", ex.Message);
        Assert.Empty(CopyFiles());
    }

    [Fact]
    public async Task Create_WorldTouchedBetweenDrops_SameSizeNewTime_NoCopy()
    {
        PutWorld(BigRows("r"));
        _host.Server.CutDownloads.Add((World, 100_000));
        var downloads = 0;
        _host.Server.OnBefore = (op, path) =>
        {
            if (op == "download" && path == World) downloads++;
            // после обрыва, перед докачкой: тот же размер, другое время — мир сохраняли
            if (op == "list" && downloads == 1) SetTime(World, _time.UtcNow.AddMinutes(5));
        };

        var ex = await Assert.ThrowsAsync<HostedWorldException>(() => Backups().CreateAsync(serverStopped: true, null, default));

        Assert.Equal(HostedWorldProblem.Changed, ex.Problem);
        Assert.Equal(1, downloads); // докачивать изменившийся мир не стали
        Assert.Empty(CopyFiles());
    }

    [Fact]
    public async Task Create_GameCopyStillBeingWritten_SaysSo()
    {
        PutWorld("live");
        PutWorldAt("/Backups/genbackup.vcdbs", BigRows("g"));
        _host.Server.OnBefore = (op, path) =>
        {
            if (op == "download" && path == "/Backups/genbackup.vcdbs") File.AppendAllText(ServerLocal(path), "more");
        };

        var ex = await Assert.ThrowsAsync<HostedWorldException>(() => Backups().CreateAsync(serverStopped: false, null, default));

        Assert.Equal(HostedWorldProblem.CopyChanged, ex.Problem);
        Assert.Contains("genbackup.vcdbs", ex.Message);
        Assert.Empty(CopyFiles());
    }

    [Fact]
    public async Task Create_Cancelled_RemovesThePart()
    {
        PutWorld(BigRows("r"));
        using var cts = new CancellationTokenSource();
        _host.Server.OnBefore = (op, path) => { if (op == "download" && path == World) cts.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Backups().CreateAsync(serverStopped: true, null, cts.Token));

        Assert.Empty(CopyFiles());
    }

    [Fact]
    public async Task Create_ExtraArchiveFails_TheWorldCopyStays_WithAWarning()
    {
        PutWorld("one");
        _host.Server.Put("/ModConfig/carryon.json", "{}");
        _host.Server.FailOn = (op, path) => op == "download" && path.StartsWith("/ModConfig/", StringComparison.Ordinal);

        var result = await Backups().CreateAsync(serverStopped: true, null, default);

        Assert.Equal(["one"], SqliteWorld.Read(result.Backup.Path));
        Assert.False(result.Backup.HasExtra);
        AssertLike("hosted.worldExtraFailed", Assert.Single(result.Warnings));
        Assert.DoesNotContain(CopyFiles(), f => f.EndsWith(".tmp"));
    }

    [Fact]
    public async Task Create_RemovesOwnLeftoverParts_LeavesForeignOnes()
    {
        Directory.CreateDirectory(_copies);
        foreach (var name in new[] { "Hosted-2026-01-01_00-00-00.vcdbs.part", "Hosted-2026-01-01_00-00-00.vcdbs.part-wal", "Other-2026-01-01_00-00-00.vcdbs.part" })
            File.WriteAllBytes(Path.Combine(_copies, name), [1]);
        PutWorld("one");

        await Backups().CreateAsync(serverStopped: true, null, default);

        Assert.Contains("Other-2026-01-01_00-00-00.vcdbs.part", CopyFiles());
        Assert.DoesNotContain(CopyFiles(), f => f.StartsWith("Hosted-2026-01-01", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_NotEnoughSpace_IsRefusedBeforeDownloading()
    {
        PutWorld(BigRows("r"));
        var backups = new HostedWorldBackups(_host.Target) { Time = _time, FreeSpace = _ => 1024 };

        var ex = await Assert.ThrowsAsync<HostedWorldException>(() => backups.CreateAsync(serverStopped: true, null, default));

        Assert.Equal(HostedWorldProblem.NoSpace, ex.Problem);
        Assert.DoesNotContain(_host.Server.DownloadOffsets, d => d.Path == World);
        Assert.Empty(CopyFiles());
    }

    [Fact]
    public async Task Create_NotAWorld_IsNotSaved()
    {
        _host.Server.Put(World, new byte[4096]);

        var ex = await Assert.ThrowsAsync<HostedWorldException>(() => Backups().CreateAsync(serverStopped: true, null, default));

        Assert.Equal(HostedWorldProblem.NotAWorld, ex.Problem);
        Assert.Empty(CopyFiles());
    }

    // ---------- список и ротация

    [Fact]
    public async Task Create_RotatesByBackupKeep_OnlyOwnCopies()
    {
        _host.Settings.BackupKeep = 2;
        Directory.CreateDirectory(_copies);
        foreach (var foreign in new[] { "Other-2026-10-01_10-00-00.vcdbs", "manual.vcdbs", "Hosted-before-restore-2026-10-01_10-00-00.vcdbs" })
        {
            File.WriteAllBytes(Path.Combine(_copies, foreign), [1]);
            File.SetLastWriteTimeUtc(Path.Combine(_copies, foreign), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }
        PutWorld("one");
        var made = new List<HostedWorldBackup>();
        IReadOnlyList<HostedWorldBackup> pruned = [];
        for (var i = 0; i < 3; i++)
        {
            var result = await Backups().CreateAsync(serverStopped: true, null, default);
            made.Add(result.Backup);
            pruned = result.Pruned;
            _time.UtcNow = _time.UtcNow.AddMinutes(10);
        }

        Assert.Equal(made[0].Name, Assert.Single(pruned).Name);
        var left = Backups().List();
        Assert.Equal([made[2].Name, made[1].Name], left.Where(b => b.IsOwn).Select(b => b.Name));
        Assert.Equal(3, left.Count(b => !b.IsOwn)); // чужие не тронуты
        Assert.False(File.Exists(HostedWorldBackups.ExtraFor(made[0].Path)));
        Assert.False(File.Exists(HostedWorldBackups.InfoFor(made[0].Path)));
    }

    [Fact]
    public void Prune_ByFileTime_AndDelete_TakeTheArchiveAlong()
    {
        Directory.CreateDirectory(_copies);
        var names = new[] { "Hosted-2026-10-01_10-00-00", "Hosted-2026-10-02_10-00-00", "Hosted-2026-10-03_10-00-00" };
        for (var i = 0; i < names.Length; i++)
            foreach (var ext in new[] { ".vcdbs", ".extra.zip", ".info.json" })
            {
                var path = Path.Combine(_copies, names[i] + ext);
                File.WriteAllText(path, ext == ".info.json" ? """{ "Origin": "World" }""" : "x");
                File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 1 + i, 8, 0, 0, DateTimeKind.Utc));
            }
        var backups = Backups();

        var removed = backups.Prune(2);

        Assert.Equal(names[0] + ".vcdbs", Assert.Single(removed).Name);
        Assert.Equal(6, CopyFiles().Length);
        var newest = backups.List()[0];
        Assert.Equal((names[2] + ".vcdbs", HostedWorldOrigin.World, 1L), (newest.Name, newest.Origin, newest.ExtraSize));

        backups.Delete(newest);
        Assert.Equal([names[1] + ".extra.zip", names[1] + ".info.json", names[1] + ".vcdbs"], CopyFiles());
        Assert.Throws<ArgumentException>(() => backups.Delete(newest with { Path = Path.Combine(_host.Dir, "elsewhere.vcdbs") }));
        Assert.Empty(backups.Prune(0));
    }

    [Fact]
    public void Folder_DefaultIsInTheDataFolder_UnderASafeName_OwnFolderOnlyAsAFullPath()
    {
        Assert.Equal(Path.Combine(AppPaths.Root, "HostedBackups", "My__server"), HostedWorldBackups.DefaultDir("My: server"));

        _host.Settings.BackupDir = "relative/dir";
        Assert.Equal(HostedWorldBackups.DefaultDir("Hosted"), new HostedWorldBackups(_host.Target).Dir);

        _host.Settings.BackupDir = _copies;
        Assert.Equal(_copies, new HostedWorldBackups(_host.Target).Dir);
        Assert.Null(HostedWorldBackups.CheckDir(_copies));
        Assert.NotNull(HostedWorldBackups.CheckDir("relative/dir"));
    }

    // ---------- восстановление

    /// <summary>Копия с миром «v1» и настройками того времени; потом на сервере всё стало «v2».</summary>
    private async Task<HostedWorldBackup> CopyThenChangeAsync()
    {
        PutWorld("v1");
        _host.Server.Put("/ModConfig/carryon.json", "v1");
        _host.Server.Put("/ModData/xskills/data.bin", "v1");
        _host.Server.Put("/Saves/XLeveling/progress.json", "v1");
        _host.Server.Put("/Playerdata/playerdata.json", "v1");
        var copy = (await Backups().CreateAsync(serverStopped: true, null, default)).Backup;
        PutWorld("v2");
        _host.Server.Put("/ModConfig/carryon.json", "v2");
        _host.Server.Put("/ModData/xskills/data.bin", "v2");
        _host.Server.Put("/Saves/XLeveling/progress.json", "v2");
        _host.Server.Put("/Playerdata/playerdata.json", "v2");
        var config = JObject.Parse(_host.Server.Text("/serverconfig.json"));
        config["ServerName"] = "changed after the copy";
        _host.Server.Put("/serverconfig.json", config.ToString());
        // остатки журнала прежнего мира: с восстановленным они бы его испортили
        _host.Server.Put(World + "-wal", [1, 2, 3]);
        _host.Server.Put(World + "-shm", [4, 5, 6]);
        _host.Server.Writes.Clear();
        return copy;
    }

    private static readonly HostedRestoreOptions Stopped = new() { ServerStopped = true };

    private string SwapMarkFile => Path.Combine(HostedJournal.DirFor(_host.Profile.Id), "world-swap.json");

    [Fact]
    public async Task Restore_PutsTheCopyInPlace_ThroughBak_AndCleansUp()
    {
        var copy = await CopyThenChangeAsync();
        var config = _host.Server.Text("/serverconfig.json");
        var steps = new List<HostedTransfer>();

        var result = await Backups().RestoreAsync(copy, Stopped, new SyncProgress<HostedTransfer>(steps.Add), default);

        Assert.Equal(["v1"], ServerRows());
        Assert.Equal(World, result.WorldPath);
        Assert.Null(result.KeptOldWorld);
        Assert.Equal(["MyVintageStoryWorld.vcdbs", "XLeveling"], _host.Server.Names("/Saves")); // ни tmp, ни bak, ни журнала
        Assert.Equal("v2", _host.Server.Text("/ModConfig/carryon.json")); // настройки — только по флагу
        Assert.Equal(config, _host.Server.Text("/serverconfig.json"));
        Assert.Equal($"upload {Tmp}", _host.Server.Writes[0]);
        Assert.Contains($"rename {World} → {Bak}", _host.Server.Writes);
        Assert.Contains($"rename {Tmp} → {World}", _host.Server.Writes);
        Assert.Contains($"rename {World}-wal → {World}-wal.evistool.bak", _host.Server.Writes);
        Assert.Equal(steps[^1].Total, steps[^1].Done);
        Assert.All(steps, s => Assert.Equal(HostedBackupStep.Upload, s.Step));
        Assert.False(File.Exists(SwapMarkFile));
    }

    [Fact]
    public async Task Restore_KeepOldWorld_LeavesItAsBak()
    {
        var copy = await CopyThenChangeAsync();

        var result = await Backups().RestoreAsync(copy, Stopped with { KeepOldWorld = true }, null, default);

        Assert.Equal(Bak, result.KeptOldWorld);
        Assert.Equal(["v1"], ServerRows());
        Assert.Equal(["v2"], SqliteWorld.Read(ServerLocal(Bak)));
    }

    [Fact]
    public async Task Restore_WithoutStoppedServer_IsRefused_AndTouchesNothing()
    {
        var copy = await CopyThenChangeAsync();
        var calls = _host.Server.Calls.Values.Sum();

        var ex = await Assert.ThrowsAsync<HostedWorldException>(() => Backups().RestoreAsync(copy, new HostedRestoreOptions(), null, default));

        Assert.Equal(HostedWorldProblem.NotStopped, ex.Problem);
        HostedTestText.AssertIs("hosted.worldRestoreRunning", ex.Message);
        Assert.Equal(calls, _host.Server.Calls.Values.Sum()); // к серверу даже не обращались
        Assert.Equal(["v2"], ServerRows());
    }

    public static TheoryData<string, string> FailingSteps => new()
    {
        { "upload", Tmp },
        { "rename", World },
        { "rename", World + "-wal" },
        { "rename", World + "-shm" },
        { "rename", Tmp },
    };

    [Theory, MemberData(nameof(FailingSteps))]
    public async Task Restore_FailsOnAnyStep_TheServerKeepsItsWorld(string op, string path)
    {
        var copy = await CopyThenChangeAsync();
        _host.Server.FailOn = (o, p) => o == op && p == path;

        var ex = await Assert.ThrowsAsync<HostedWorldException>(() => Backups().RestoreAsync(copy, Stopped, null, default));

        Assert.Equal(HostedWorldProblem.RestoreUndone, ex.Problem);
        Assert.Equal(["v2"], ServerRows());
        Assert.Equal([3L, 3L], new[] { "-wal", "-shm" }.Select(t => new FileInfo(ServerLocal(World + t)).Length)); // журнал на месте
        Assert.False(_host.Server.Has(Tmp));
        Assert.False(_host.Server.Has(Bak));
        Assert.False(File.Exists(SwapMarkFile));
    }

    [Fact]
    public async Task Restore_ConnectionDropsOnTheLastRename_ReconnectsAndPutsTheWorldBack()
    {
        var copy = await CopyThenChangeAsync();
        _host.Server.OnBefore = (op, path) => { if (op == "rename" && path == Tmp) _host.Server.DropNext = true; };

        var ex = await Assert.ThrowsAsync<HostedWorldException>(() => Backups().RestoreAsync(copy, Stopped, null, default));

        Assert.Equal(HostedWorldProblem.RestoreUndone, ex.Problem);
        Assert.Equal(["v2"], ServerRows());
        Assert.False(_host.Server.Has(Bak));
        Assert.False(_host.Server.Has(Tmp));
    }

    [Fact]
    public async Task Restore_PutBackFailsToo_IsStuck_TheNextOperationReturnsTheWorld()
    {
        var copy = await CopyThenChangeAsync();
        var broken = true;
        _host.Server.FailOn = (op, path) => broken && op == "rename" && path is Tmp or Bak;

        var stuck = await Assert.ThrowsAsync<HostedWorldException>(() => Backups().RestoreAsync(copy, Stopped, null, default));

        Assert.Equal(HostedWorldProblem.RestoreStuck, stuck.Problem);
        Assert.Contains(Bak, stuck.Message);
        Assert.False(_host.Server.Has(World)); // мир сейчас только в bak — его вернёт следующая операция
        Assert.True(_host.Server.Has(Bak));
        Assert.True(File.Exists(SwapMarkFile));

        broken = false;
        _time.UtcNow = _time.UtcNow.AddMinutes(1);
        // журнал прежнего мира вернулся на место вместе с ним; для копии он мусорный — убираем, чтобы копия не сводила его
        foreach (var tail in new[] { "-wal", "-shm" }) File.Delete(ServerLocal(World + tail));
        var next = await Backups().CreateAsync(serverStopped: true, null, default);

        HostedTestText.AssertIs("hosted.worldRecovered", Assert.Single(next.Warnings));
        Assert.Equal(["v2"], ServerRows());
        Assert.Equal(["v2"], SqliteWorld.Read(next.Backup.Path));
        Assert.False(_host.Server.Has(Tmp));
        Assert.False(File.Exists(SwapMarkFile));
    }

    [Fact]
    public async Task Restore_Extras_ByFlags_ServerConfigOnlyWhenAsked()
    {
        var copy = await CopyThenChangeAsync();
        var original = await ReadExtraAsync(copy, "serverconfig.json");
        var changed = _host.Server.Text("/serverconfig.json");
        _host.Server.Put("/ModData/newmod/fresh.json", "stays"); // чего нет в архиве — не удаляется

        var result = await Backups().RestoreAsync(copy, Stopped with { Extras = HostedWorldExtras.ModConfig | HostedWorldExtras.ModData }, null, default);

        Assert.Equal(3, result.ExtraFiles);
        Assert.Equal("v1", _host.Server.Text("/ModConfig/carryon.json"));
        Assert.Equal("v1", _host.Server.Text("/ModData/xskills/data.bin"));
        Assert.Equal("v1", _host.Server.Text("/Saves/XLeveling/progress.json"));
        Assert.Equal("v2", _host.Server.Text("/Playerdata/playerdata.json"));
        Assert.Equal("stays", _host.Server.Text("/ModData/newmod/fresh.json"));
        Assert.Equal(changed, _host.Server.Text("/serverconfig.json"));
        Assert.Empty(result.Warnings);

        PutWorld("v2");
        var withConfig = await Backups().RestoreAsync(copy, Stopped with { Extras = HostedWorldExtras.ServerConfig | HostedWorldExtras.Playerdata }, null, default);
        Assert.Equal(2, withConfig.ExtraFiles);
        Assert.Equal(original, _host.Server.Text("/serverconfig.json"));
        Assert.Equal("v1", _host.Server.Text("/Playerdata/playerdata.json"));
        Assert.True(_host.Server.Has("/serverconfig.json.evistool.bak")); // прежний конфиг — страховкой, как при правке модов
    }

    private static Task<string> ReadExtraAsync(HostedWorldBackup copy, string entry)
    {
        using var zip = ZipFile.OpenRead(HostedWorldBackups.ExtraFor(copy.Path));
        using var reader = new StreamReader(zip.GetEntry(entry)!.Open());
        return Task.FromResult(reader.ReadToEnd());
    }

    [Fact]
    public async Task Restore_BrokenArchive_IsRefusedBeforeTheServerIsTouched()
    {
        var copy = await CopyThenChangeAsync();
        File.WriteAllBytes(HostedWorldBackups.ExtraFor(copy.Path), [1, 2, 3, 4]);

        var ex = await Assert.ThrowsAsync<HostedWorldException>(() =>
            Backups().RestoreAsync(copy, Stopped with { Extras = HostedWorldExtras.ModConfig }, null, default));

        Assert.Equal(HostedWorldProblem.BadArchive, ex.Problem);
        Assert.Empty(_host.Server.Writes);
        Assert.Equal(["v2"], ServerRows());
    }

    [Fact]
    public async Task Restore_CopyWithoutArchive_RestoresTheWorld_AndSaysTheRestIsNot()
    {
        var copy = await CopyThenChangeAsync();
        File.Delete(HostedWorldBackups.ExtraFor(copy.Path));

        var result = await Backups().RestoreAsync(copy, Stopped with { Extras = HostedWorldExtras.ModConfig }, null, default);

        Assert.Equal(["v1"], ServerRows());
        HostedTestText.AssertIs("hosted.worldNoExtra", Assert.Single(result.Warnings));
    }

    [Fact]
    public async Task Restore_OntoAServerWithoutAWorld_PutsItInPlace()
    {
        var copy = await CopyThenChangeAsync();
        foreach (var tail in new[] { "", "-wal", "-shm" }) File.Delete(ServerLocal(World + tail));

        var result = await Backups().RestoreAsync(copy, Stopped with { KeepOldWorld = true }, null, default);

        Assert.Equal(["v1"], ServerRows());
        Assert.Null(result.KeptOldWorld); // прежнего мира не было
    }

    [Fact]
    public async Task Restore_CancelledDuringUpload_TheWorldStays_NoTmp()
    {
        var copy = await CopyThenChangeAsync();
        using var cts = new CancellationTokenSource();
        _host.Server.OnBefore = (op, path) => { if (op == "upload" && path == Tmp) cts.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Backups().RestoreAsync(copy, Stopped, null, cts.Token));

        Assert.Equal(["v2"], ServerRows());
        Assert.False(_host.Server.Has(Tmp));
        Assert.False(_host.Server.Has(Bak));
    }

    /// <summary>Отметка оборванной подмены, как её оставил бы процесс, закрытый посреди восстановления.</summary>
    private void LeaveSwapMark(long size) =>
        new HostedWorldBackups.SwapMark { World = World, Tmp = Tmp, Size = size, Moves = [[World, Bak]] }.Save(_host.Profile.Id);

    [Fact]
    public async Task Recover_NewWorldHadLanded_RemovesTheLeftoverBak()
    {
        PutWorld("new");
        PutWorldAt(Bak, "old");
        LeaveSwapMark(new FileInfo(ServerLocal(World)).Length);

        var result = await Backups().CreateAsync(serverStopped: true, null, default);

        Assert.Empty(result.Warnings);
        Assert.False(_host.Server.Has(Bak));
        Assert.Equal(["new"], ServerRows());
        Assert.False(File.Exists(SwapMarkFile));
    }

    [Fact]
    public async Task Recover_AnotherWorldIsInPlace_KeepsThePreviousOne_AndSaysWhere()
    {
        PutWorld("created-by-the-server");
        PutWorldAt(Bak, "old");
        LeaveSwapMark(new FileInfo(ServerLocal(World)).Length + 4096);

        var result = await Backups().CreateAsync(serverStopped: true, null, default);

        AssertLike("hosted.worldBakLeft", Assert.Single(result.Warnings));
        Assert.Contains(Bak, result.Warnings[0]);
        Assert.Equal(["old"], SqliteWorld.Read(ServerLocal(Bak)));
        Assert.False(File.Exists(SwapMarkFile));
    }

    [Fact]
    public async Task Locate_RelativePathInTheConfig_IsFromTheDataFolder()
    {
        var config = JObject.Parse(_host.Server.Text("/serverconfig.json"));
        config["WorldConfig"]!["SaveFileLocation"] = "Saves/Relative.vcdbs";
        _host.Server.Put("/serverconfig.json", config.ToString());
        PutWorldAt("/Saves/Relative.vcdbs", "r");

        var world = await Backups().LocateAsync(default);

        Assert.Equal((HostedWorldFoundBy.Config, "/Saves/Relative.vcdbs", true), (world.FoundBy, world.Path, world.Exists));
    }

    [Fact]
    public async Task Locate_ConfigPointsAtNotAWorld_IsIgnored()
    {
        // мусор в конфиге не должен стать местом, куда восстановление зальёт мир
        var config = JObject.Parse(_host.Server.Text("/serverconfig.json"));
        config["WorldConfig"]!["SaveFileLocation"] = "//home//sid_1234567//vintageStory//serverconfig.json";
        _host.Server.Put("/serverconfig.json", config.ToString());
        PutWorld("a");

        var world = await Backups().LocateAsync(default);

        Assert.Equal((HostedWorldFoundBy.Log, World), (world.FoundBy, world.Path));
    }

    [Fact]
    public async Task DownloadFrom_FallbackForOtherTransports_ReadsFromThePositionToTheEnd()
    {
        var bytes = new byte[3 * 1024 * 1024 + 123];
        new Random(5).NextBytes(bytes);
        _host.Server.Put("/big.bin", bytes);
        await _host.Server.ConnectAsync(default);
        IRemoteFiles plain = new PlainFiles(_host.Server);
        using var tail = new MemoryStream();

        await plain.DownloadFromAsync("/big.bin", 1_000_000, tail, null, default);

        Assert.Equal(bytes.AsSpan(1_000_000).ToArray(), tail.ToArray());
    }

    /// <summary>Транспорт без своей докачки — берёт запасную из <see cref="IRemoteFiles"/>.</summary>
    private sealed class PlainFiles(IRemoteFiles inner) : IRemoteFiles
    {
        public RemoteFeatures Features => inner.Features;
        public bool IsConnected => inner.IsConnected;
        public Task ConnectAsync(CancellationToken ct) => inner.ConnectAsync(ct);
        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string dir, CancellationToken ct) => inner.ListAsync(dir, ct);
        public Task<RemoteEntry?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);
        public Task<byte[]> ReadRangeAsync(string path, long offset, int count, CancellationToken ct) => inner.ReadRangeAsync(path, offset, count, ct);
        public Task DownloadAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken ct) => inner.DownloadAsync(path, destination, progress, ct);
        public Task UploadAsync(Stream source, string path, IProgress<long>? progress, CancellationToken ct) => inner.UploadAsync(source, path, progress, ct);
        public Task RenameAsync(string from, string to, CancellationToken ct) => inner.RenameAsync(from, to, ct);
        public Task DeleteFileAsync(string path, CancellationToken ct) => inner.DeleteFileAsync(path, ct);
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Restore_NotAWorld_IsRefused()
    {
        var copy = await CopyThenChangeAsync();
        File.WriteAllBytes(copy.Path, new byte[100]);

        var ex = await Assert.ThrowsAsync<HostedWorldException>(() => Backups().RestoreAsync(copy, Stopped, null, default));

        Assert.Equal(HostedWorldProblem.NotAWorld, ex.Problem);
        Assert.Empty(_host.Server.Writes);
    }
}

/// <summary>Ход — сразу и в том же потоке (Progress&lt;T&gt; отдаёт его потом и в пул).</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

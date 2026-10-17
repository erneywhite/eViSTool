using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>Скан, установка, удаление и отпечаток модов сервера у хостера — на поддельном FTP «как G-Portal».</summary>
public sealed class HostedModsTests : IDisposable
{
    private readonly HostedFixture _host = new();

    public HostedModsTests() => _host.GPortalLayout();

    public void Dispose() => _host.Dispose();

    private Task<(ResolvedProfile Profile, IReadOnlyList<LocalMod> Mods)> ScanAsync() => ModTargets.ScanAsync(_host.Target);

    private string[] ServerMods() => _host.Server.Names("/Mods").Where(n => n.EndsWith(".zip") || n.Contains(".evistool")).ToArray();

    private JArray? Disabled() => JObject.Parse(_host.Server.Text("/serverconfig.json"))["WorldConfig"]?["DisabledMods"] as JArray;

    // ---------- скан

    [Fact]
    public async Task Scan_ShowsModsAndTheHostersMod_HidesBaseFiles()
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        _host.PutMod("/Mods/beta_2.0.0.zip", "beta", "2.0.0");

        var (profile, mods) = await ScanAsync();

        Assert.True(profile.OffMachine);
        Assert.Equal("1.22.7", profile.GameVersion?.ToString());
        Assert.Equal("/serverconfig.json", profile.ConfigPath);
        Assert.Equal("/Mods", profile.InstallDir);
        Assert.Equal(["/Mods/alpha_1.0.0.zip", "/Mods/beta_2.0.0.zip", "/gportal_mods/GPortalPlayerCount.cs"], mods.Select(m => m.Path).Order(StringComparer.Ordinal));
        var hoster = mods.Single(m => m.Path.EndsWith(".cs"));
        Assert.Null(hoster.Info);
        HostedTestText.AssertIs("hosted.hosterMod", hoster.Error!);
        Assert.Equal("1.0.0", mods.Single(m => m.Info?.ModId == "alpha").Info!.Version);
    }

    /// <summary>Какие файлы читались (скачивались или кусками) с этого момента.</summary>
    private List<string> TrackReads()
    {
        var touched = new List<string>();
        _host.Server.OnBefore = (op, path) => { if (op is "download" or "read") lock (touched) touched.Add(path); };
        return touched;
    }

    [Fact]
    public async Task Scan_SecondTimeTakesModInfoFromTheCache()
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var first = TrackReads();
        await ScanAsync();
        Assert.Contains("/Mods/alpha_1.0.0.zip", first);

        var second = TrackReads();
        var (_, mods) = await ScanAsync();

        Assert.Contains(mods, m => m.Info?.ModId == "alpha");
        Assert.DoesNotContain("/Mods/alpha_1.0.0.zip", second); // конфиг, version.txt и лог читаются заново, архив — нет
        Assert.Contains("/serverconfig.json", second);
    }

    [Fact]
    public async Task Scan_ReportsReadingProgressForArchivesNotInTheCache()
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        _host.PutMod("/Mods/beta_1.0.0.zip", "beta", "1.0.0");
        var lines = new List<string>();

        await new HostedMods(_host.Target).ScanAsync(new SyncProgress(lines.Add), default);

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Contains("2", l));
    }

    [Fact]
    public async Task Scan_MarksDisabledMods_AndTellsWhereTheVersionCameFrom()
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var config = JObject.Parse(_host.Server.Text("/serverconfig.json"));
        config["WorldConfig"]!["DisabledMods"] = new JArray("alpha");
        _host.Server.Put("/serverconfig.json", config.ToString());

        var hosted = new HostedMods(_host.Target);
        var (profile, mods) = await hosted.ScanAsync(null, default);

        Assert.True(mods.Single(m => m.Info?.ModId == "alpha").IsDisabled);
        Assert.Equal(["alpha"], profile.DisabledMods);
        Assert.Equal(GameVersionSource.VersionFile, hosted.VersionSource);
        Assert.Equal("/Mods", hosted.Layout!.InstallDir);
    }

    [Fact]
    public async Task Scan_BigArchive_OnAServerWithoutRangeReadsAndWithListTimes()
    {
        // vsftpd-подобный: время в LIST до минуты и в поясе сервера, REST не работает — архив качается целиком во
        // временный файл, который потом исчезает
        _host.Server.NoRange = true;
        _host.Server.ListLocalTime = true;
        using (var ms = new MemoryStream())
        {
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open()))
                    w.Write("""{ "modid": "big", "name": "Big", "version": "3.0.0" }""");
                var noise = new byte[400 * 1024];
                new Random(7).NextBytes(noise);
                using var s = zip.CreateEntry("assets/noise.bin", System.IO.Compression.CompressionLevel.NoCompression).Open();
                s.Write(noise);
            }
            _host.Server.Put("/Mods/big_3.0.0.zip", ms.ToArray());
        }

        var (_, mods) = await ScanAsync();
        var (_, again) = await ScanAsync();

        Assert.Equal("3.0.0", mods.Single(m => m.Info?.ModId == "big").Info!.Version);
        Assert.Equal("3.0.0", again.Single(m => m.Info?.ModId == "big").Info!.Version);
        var dl = Path.Combine(ModInfoCache.DirFor(_host.Profile.Id), "dl");
        Assert.True(!Directory.Exists(dl) || Directory.GetFiles(dl).Length == 0);
    }

    // ---------- установка

    [Fact]
    public async Task Install_NewMod_LandsInMods_WithoutLeftovers()
    {
        var outcome = await ModTargets.InstallAsync(_host.Target, _host.Zip("alpha_1.0.0.zip", "alpha", "1.0.0"));

        Assert.Equal("alpha 1.0.0", outcome!.Text);
        Assert.Equal(["alpha_1.0.0.zip"], ServerMods());
        Assert.Null(HostedJournal.Read(_host.Profile.Id));
        var history = ModHistory.ReadEntries(ModHistory.FileFor(_host.Profile.Id));
        Assert.Equal(("alpha", (string?)null, "1.0.0"), (history.Single().ModId, history.Single().From, history.Single().To));
        // кэш пополнен сразу: следующий скан архив не читает
        var reads = TrackReads();
        var (_, mods) = await ScanAsync();
        Assert.Contains(mods, m => m.Info?.ModId == "alpha");
        Assert.DoesNotContain("/Mods/alpha_1.0.0.zip", reads);
    }

    [Fact]
    public async Task Update_ReplacesTheOldVersion_KeepsACopyAndWritesHistory()
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");

        var outcome = await ModTargets.InstallAsync(_host.Target, _host.Zip("alpha_1.1.0.zip", "alpha", "1.1.0"));

        Assert.Equal("alpha: 1.0.0 → 1.1.0", outcome!.Text);
        Assert.Equal(["alpha_1.1.0.zip"], ServerMods());
        var kept = Assert.Single(ModBackupStore.ForProfile(_host.Profile).List("alpha"));
        Assert.Equal("1.0.0", ModScanner.ReadZip(kept).Info!.Version);
        var entry = ModHistory.ReadEntries(ModHistory.FileFor(_host.Profile.Id)).Single();
        Assert.Equal(("1.0.0", "1.1.0"), (entry.From, entry.To));
        // G-Portal не переименовывает поверх: сначала удалили старую, потом переименовали новую
        var writes = _host.Server.Writes;
        Assert.True(writes.IndexOf("delete /Mods/alpha_1.0.0.zip") < writes.IndexOf("rename /Mods/alpha_1.1.0.zip.evistool.tmp → /Mods/alpha_1.1.0.zip"));
    }

    [Fact]
    public async Task Update_WithTheSameFileName_OnAServerThatDoesNotRenameOver()
    {
        _host.PutMod("/Mods/alpha.zip", "alpha", "1.0.0");

        await ModTargets.InstallAsync(_host.Target, _host.Zip("alpha.zip", "alpha", "1.1.0"));

        Assert.Equal(["alpha.zip"], ServerMods());
        var (_, mods) = await ScanAsync();
        Assert.Equal("1.1.0", mods.Single(m => m.Info?.ModId == "alpha").Info!.Version);
    }

    [Fact]
    public async Task Install_NameTakenByAnotherMod_GetsAFreeName()
    {
        _host.PutMod("/Mods/common.zip", "other", "1.0.0");

        await ModTargets.InstallAsync(_host.Target, _host.Zip("common.zip", "alpha", "1.0.0"));

        Assert.Equal(["common.zip", "common_alpha.zip"], ServerMods().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Update_KeepsADisabledModDisabled()
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var config = JObject.Parse(_host.Server.Text("/serverconfig.json"));
        config["WorldConfig"]!["DisabledMods"] = new JArray("alpha@1.0.0");
        _host.Server.Put("/serverconfig.json", config.ToString());

        await ModTargets.InstallAsync(_host.Target, _host.Zip("alpha_1.1.0.zip", "alpha", "1.1.0"));

        Assert.Equal(["alpha"], Disabled()!.Select(t => (string)t!));
        var (_, mods) = await ScanAsync();
        Assert.True(mods.Single(m => m.Info?.ModId == "alpha").IsDisabled);
    }

    [Fact]
    public async Task Install_Declined_ChangesNothing()
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");

        InstallPlan? seen = null;
        var outcome = await ModTargets.InstallAsync(_host.Target, _host.Zip("alpha_0.9.0.zip", "alpha", "0.9.0"), p => { seen = p; return false; });

        Assert.Null(outcome);
        Assert.True(seen!.IsDowngrade);
        Assert.Equal("/Mods/alpha_0.9.0.zip", seen.TargetPath);
        Assert.Empty(_host.Server.Writes);
    }

    [Fact]
    public async Task Install_FileChangedWhileAsking_IsRefused()
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");

        var ex = await Assert.ThrowsAsync<IOException>(() => ModTargets.InstallAsync(_host.Target, _host.Zip("alpha_1.1.0.zip", "alpha", "1.1.0"), _ =>
        {
            // пока человек отвечал, мод перезалили через FileZilla — другой размер
            _host.Server.Put("/Mods/alpha_1.0.0.zip", HostedFixture.ZipBytes("alpha", "1.0.0-hotfix-with-longer-version"));
            return true;
        }));

        Assert.Contains("alpha_1.0.0.zip", ex.Message);
        Assert.Empty(_host.Server.Writes);
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("delete")]
    [InlineData("rename")]
    public async Task Install_FailureOnAnyStep_LeavesTheServerAsItWas(string step)
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var before = File.ReadAllBytes(Path.Combine(_host.Server.Root, "Mods", "alpha_1.0.0.zip"));
        var failed = false; // только первый раз: откат после сбоя должен пройти
        _host.Server.FailOn = (op, path) => op == step && path.StartsWith("/Mods/alpha", StringComparison.Ordinal) && !failed && (failed = true);

        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() =>
            ModTargets.InstallAsync(_host.Target, _host.Zip("alpha_1.1.0.zip", "alpha", "1.1.0")));

        Assert.Equal(RemoteFailure.Denied, ex.Kind);
        Assert.Equal(["alpha_1.0.0.zip"], ServerMods());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_host.Server.Root, "Mods", "alpha_1.0.0.zip")));
        Assert.Null(HostedJournal.Read(_host.Profile.Id));
        Assert.Empty(ModHistory.ReadEntries(ModHistory.FileFor(_host.Profile.Id)));
        Assert.Empty(ModBackupStore.ForProfile(_host.Profile).List("alpha"));
    }

    [Fact]
    public async Task Install_ConnectionLostBeforeRename_TheNextScanFinishesIt()
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        _host.Server.OnBefore = (op, _) => { if (op == "rename") _host.Server.DropNext = true; };

        await Assert.ThrowsAsync<RemoteFilesException>(() => ModTargets.InstallAsync(_host.Target, _host.Zip("alpha_1.1.0.zip", "alpha", "1.1.0")));
        _host.Server.OnBefore = null;

        // старая удалена, новая лежит временным файлом, откат не дошёл — журнал остался
        Assert.Equal(["alpha_1.1.0.zip.evistool.tmp"], ServerMods());
        Assert.NotNull(HostedJournal.Read(_host.Profile.Id));

        var (profile, mods) = await ScanAsync();

        Assert.Equal(["alpha_1.1.0.zip"], ServerMods());
        Assert.Equal("1.1.0", mods.Single(m => m.Info?.ModId == "alpha").Info!.Version);
        Assert.Contains(profile.Warnings, w => HostedTestText.Of("hosted.recovered").Select(t => t.Replace("{0}", "alpha")).Contains(w));
        Assert.Null(HostedJournal.Read(_host.Profile.Id));
        Assert.Single(ModBackupStore.ForProfile(_host.Profile).List("alpha"));
        Assert.Equal("1.1.0", ModHistory.ReadEntries(ModHistory.FileFor(_host.Profile.Id)).Single().To);
    }

    [Fact]
    public async Task Install_ConnectionLostAfterDeletingTheOldOnly_UploadIncomplete_TheNextScanRestoresTheOld()
    {
        // журнал есть, временный файл недолит (или пропал), старой версии на сервере нет — вернуть старую из копии
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var copyDir = Directory.CreateDirectory(Path.Combine(HostedJournal.DirFor(_host.Profile.Id), "old", "t1")).FullName;
        var copy = Path.Combine(copyDir, "alpha_1.0.0.zip");
        File.Copy(Path.Combine(_host.Server.Root, "Mods", "alpha_1.0.0.zip"), copy);
        File.Delete(Path.Combine(_host.Server.Root, "Mods", "alpha_1.0.0.zip"));
        _host.Server.Put("/Mods/alpha_1.1.0.zip.evistool.tmp", [1, 2, 3]);
        HostedJournal.Write(_host.Profile.Id, new HostedPending("/Mods/alpha_1.1.0.zip.evistool.tmp", "/Mods/alpha_1.1.0.zip", 999,
            [new HostedPendingOld("/Mods/alpha_1.0.0.zip", copy, new FileInfo(copy).Length)], "alpha", "alpha", "1.0.0", "1.1.0"));

        var (profile, mods) = await ScanAsync();

        Assert.Equal(["alpha_1.0.0.zip"], ServerMods());
        Assert.Equal("1.0.0", mods.Single(m => m.Info?.ModId == "alpha").Info!.Version);
        Assert.Contains(profile.Warnings, w => HostedTestText.Of("hosted.rolledBack").Select(t => t.Replace("{0}", "alpha")).Contains(w));
        Assert.Null(HostedJournal.Read(_host.Profile.Id));
        Assert.False(Directory.Exists(copyDir));
        Assert.Empty(ModHistory.ReadEntries(ModHistory.FileFor(_host.Profile.Id)));
    }

    [Fact]
    public async Task Install_IntoTheHostersFolder_IsRefused()
    {
        _host.PutMod("/gportal_mods/hosterzip.zip", "hosterzip", "1.0.0");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModTargets.InstallAsync(_host.Target, _host.Zip("hosterzip_2.0.0.zip", "hosterzip", "2.0.0")));

        HostedTestText.AssertIs("hosted.hosterMod", ex.Message);
        Assert.Empty(_host.Server.Writes);
    }

    // ---------- удаление

    [Fact]
    public async Task Delete_KeepsACopy_WritesHistory_AndForgetsTheDisabledEntry()
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var config = JObject.Parse(_host.Server.Text("/serverconfig.json"));
        config["WorldConfig"]!["DisabledMods"] = new JArray("alpha", "other");
        _host.Server.Put("/serverconfig.json", config.ToString());
        var (_, mods) = await ScanAsync();

        await ModTargets.DeleteAsync(_host.Target, mods.Single(m => m.Info?.ModId == "alpha"), _ => throw new InvalidOperationException("no local trash"));

        Assert.Empty(ServerMods());
        Assert.Single(ModBackupStore.ForProfile(_host.Profile).List("alpha"));
        var entry = ModHistory.ReadEntries(ModHistory.FileFor(_host.Profile.Id)).Single();
        Assert.Equal(("alpha", "1.0.0", (string?)null), (entry.ModId, entry.From, entry.To));
        Assert.Equal(["other"], Disabled()!.Select(t => (string)t!));
    }

    [Fact]
    public async Task Delete_TheHostersMod_IsRefused()
    {
        var (_, mods) = await ScanAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModTargets.DeleteAsync(_host.Target, mods.Single(m => m.Path.EndsWith(".cs")), _ => { }));

        HostedTestText.AssertIs("hosted.hosterMod", ex.Message);
        Assert.True(_host.Server.Has("/gportal_mods/GPortalPlayerCount.cs"));
    }

    [Fact]
    public async Task Delete_AModThatIsGone_SaysSo()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            ModTargets.DeleteAsync(_host.Target, new LocalMod("/Mods/nothing.zip", null, null), _ => { }));
    }

    // ---------- отпечаток

    [Fact]
    public async Task Stamp_ChangesWhenSomeoneAddsAMod_AndOnlyThen()
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        await ScanAsync();
        var first = await ModTargets.StampAsync(_host.Target);
        Assert.Equal(first, await ModTargets.StampAsync(_host.Target));

        _host.PutMod("/Mods/beta_1.0.0.zip", "beta", "1.0.0"); // через FileZilla, мимо окна

        Assert.NotEqual(first, await ModTargets.StampAsync(_host.Target));
    }

    // ---------- ловушка: та же раскладка на этой машине

    [Fact]
    public async Task NoOperation_TouchesALocalFolderWithTheSamePaths()
    {
        // профиль с «папками» на этой машине, где лежит то же самое, что на сервере: операции по FTP их не трогают
        var local = Directory.CreateDirectory(Path.Combine(_host.Dir, "local")).FullName;
        Directory.CreateDirectory(Path.Combine(local, "Mods"));
        File.WriteAllBytes(Path.Combine(local, "Mods", "alpha_1.0.0.zip"), HostedFixture.ZipBytes("alpha", "1.0.0"));
        File.Copy(HostedFixture.Sample("gportal-serverconfig-after-start.json"), Path.Combine(local, "serverconfig.json"));
        _host.Profile.GameDir = local;
        _host.Profile.DataDir = local;
        var snapshot = Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllBytes);
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");

        await ModTargets.InstallAsync(_host.Target, _host.Zip("alpha_1.1.0.zip", "alpha", "1.1.0"));
        var (_, mods) = await ScanAsync();
        var alpha = mods.Single(m => m.Info?.ModId == "alpha");
        await ModTargets.SetEnabledAsync(_host.Target, alpha, enabled: false);
        await ModTargets.SetEnabledAsync(_host.Target, alpha, enabled: true);
        await ModTargets.DeleteAsync(_host.Target, alpha, _ => throw new InvalidOperationException("no local trash"));
        await ModTargets.StampAsync(_host.Target);

        var after = Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories).ToList();
        Assert.Equal(snapshot.Keys.Order(), after.Order());
        foreach (var (file, bytes) in snapshot) Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    /// <summary>Progress без контекста синхронизации: строки приходят сразу, по порядку.</summary>
    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}

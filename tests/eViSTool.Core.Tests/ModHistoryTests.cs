using System.IO.Compression;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.Core.Tests;

public sealed class ModHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public ModHistoryTests() => _file = Path.Combine(Directory.CreateDirectory(_root).FullName, "History", "p.jsonl");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ChangesInsideOneAction_AreOneOperation_OthersSeparate()
    {
        using (ModHistory.Begin(ModHistorySource.UpdateAll))
        {
            ModHistory.Record(_file, "footprints", "Footprints", "1.2.12", "1.2.13");
            ModHistory.Record(_file, "betterloot", "Better Loot", "2.0.2", "2.1.0");
        }
        ModHistory.Record(_file, "oldmod", "Old Mod", "2.0.1", null); // вне действия — отдельное, «вручную»

        var ops = ModHistory.Read(_file);
        Assert.Equal(2, ops.Count);
        var batch = Assert.Single(ops, o => o.Source == ModHistorySource.UpdateAll);
        Assert.Equal(["footprints", "betterloot"], batch.Changes.Select(c => c.ModId));
        Assert.Equal(("1.2.12", "1.2.13"), (batch.Changes[0].From, batch.Changes[0].To));
        var removed = Assert.Single(ops, o => o.Source == ModHistorySource.Manual).Changes.Single();
        Assert.Null(removed.To);
    }

    [Fact]
    public void NestedBegin_StaysInTheOuterAction_AndScopeEndsWithIt()
    {
        using (ModHistory.Begin(ModHistorySource.Pack))
        {
            var outer = ModHistory.Current!.Id;
            using (ModHistory.Begin(ModHistorySource.Zip))
                Assert.Equal((outer, ModHistorySource.Pack), (ModHistory.Current!.Id, ModHistory.Current.Source));
        }
        Assert.Null(ModHistory.Current);
    }

    [Fact]
    public async Task ActionFlowsIntoAwaitedWork_AndIsJoinedByAnotherProcess()
    {
        string id;
        using (ModHistory.Begin(ModHistorySource.Rollback, undoes: "abc123"))
        {
            id = ModHistory.Current!.Id;
            await Task.Run(() => ModHistory.Record(_file, "a", "A", "2", "1"));
        }
        // агент получил действие окна в заголовках
        using (ModHistory.Join(id, ModHistorySource.Rollback, "abc123"))
            ModHistory.Record(_file, "b", "B", "3", "2");

        var op = Assert.Single(ModHistory.Read(_file));
        Assert.Equal((id, "abc123", 2), (op.Id, op.Undoes, op.Changes.Count));
    }

    [Theory]
    [InlineData("../../etc", "zip")]
    [InlineData("", "zip")]
    [InlineData("good-id", "bad source!")]
    public void Join_RejectsJunk(string id, string source)
    {
        using (ModHistory.Join(id, source))
        {
            var scope = ModHistory.Current!;
            if (id == "good-id") Assert.Equal((id, ModHistorySource.Manual), (scope.Id, scope.Source));
            else Assert.NotEqual(id, scope.Id);
        }
    }

    [Fact]
    public void Read_SkipsOldAndBrokenLines_NewestFirst()
    {
        var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        ModHistory.Record(_file, "old", null, "1", "2", at: now.AddDays(-100));
        ModHistory.Record(_file, "first", null, "1", "2", at: now.AddDays(-2));
        File.AppendAllText(_file, "{ это не json\n");
        ModHistory.Record(_file, "second", null, "1", "2", at: now.AddHours(-1));

        var ops = ModHistory.Read(_file, now: now);
        Assert.Equal(["second", "first"], ops.Select(o => o.Changes.Single().ModId));
        Assert.Equal(DateTimeKind.Utc, ops[0].At.Kind);
    }

    [Fact]
    public void OldEntries_ArePrunedFromTheFile()
    {
        ModHistory.Record(_file, "ancient", null, "1", "2", at: DateTime.UtcNow.AddDays(-ModHistory.KeepDays - 30));
        ModHistory.Record(_file, "fresh", null, "1", "2");
        Assert.Equal(["fresh"], ModHistory.ReadEntries(_file).Select(e => e.ModId));
    }

    [Fact]
    public void Installer_WritesUpdatesAndNewMods_IntoTheProfileHistory()
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        var mods = Directory.CreateDirectory(Path.Combine(data, "Mods")).FullName;
        var incoming = Directory.CreateDirectory(Path.Combine(_root, "in")).FullName;
        var profile = ProfileResolver.Resolve(new GameProfile { Kind = ProfileKind.Client, DataDir = data });
        var store = new ModBackupStore(Path.Combine(_root, "backups"), historyFile: _file);

        MakeZip(mods, "carry_1.0.0.zip", "carryon", "1.0.0");
        using (ModHistory.Begin(ModHistorySource.UpdateAll))
        {
            var update = ModInstaller.Plan(MakeZip(incoming, "carry_1.1.0.zip", "carryon", "1.1.0"), profile, ModUpdateService.ScanLocal(profile));
            ModInstaller.Apply(update, store);
            var fresh = ModInstaller.Plan(MakeZip(incoming, "new_2.0.0.zip", "newmod", "2.0.0"), profile, ModUpdateService.ScanLocal(profile));
            ModInstaller.Apply(fresh, store);
        }

        var op = Assert.Single(ModHistory.Read(_file));
        Assert.Equal(ModHistorySource.UpdateAll, op.Source);
        Assert.Equal([("carryon", "1.0.0", "1.1.0"), ("newmod", null, "2.0.0")],
            op.Changes.Select(c => (c.ModId, c.From, c.To)));
    }

    private static string MakeZip(string dir, string fileName, string modId, string version)
    {
        var path = Path.Combine(dir, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open());
        w.Write($$"""{ "modid": "{{modId}}", "name": "{{modId}}", "version": "{{version}}" }""");
        return path;
    }
}

using System.IO.Compression;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.Core.Tests;

public sealed class ModHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;
    private static readonly DateTime T0 = new(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);

    public ModHistoryTests() => _file = Path.Combine(Directory.CreateDirectory(_root).FullName, "History", "p.jsonl");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void EverythingBetweenTwoLaunches_IsOneSession()
    {
        // утром: «Обновить всё», потом ещё мод из каталога — и запуск игры
        using (ModHistory.Begin(ModHistorySource.UpdateAll))
        {
            ModHistory.Record(_file, "footprints", "Footprints", "1.2.12", "1.2.13", T0);
            ModHistory.Record(_file, "betterloot", "Better Loot", "2.0.2", "2.1.0", T0.AddMinutes(1));
        }
        using (ModHistory.Begin(ModHistorySource.Catalog))
            ModHistory.Record(_file, "newmod", "New Mod", null, "1.0.0", T0.AddMinutes(10));
        ModHistory.MarkLaunch(_file, T0.AddMinutes(12));
        ModHistory.MarkLaunch(_file, T0.AddMinutes(90)); // запуск без изменений — второй отметки нет
        // после игры удалили мод — уже следующий сеанс
        ModHistory.Record(_file, "oldmod", "Old Mod", "2.0.1", null, T0.AddMinutes(95));

        var sessions = ModHistory.Read(_file, T0.AddHours(2));
        Assert.Equal(2, sessions.Count);
        var morning = sessions[1];
        Assert.Equal(T0.AddMinutes(12), morning.LaunchedAt);
        Assert.Equal(["footprints", "betterloot", "newmod"], morning.Changes.Select(c => c.ModId));
        Assert.Equal([ModHistorySource.UpdateAll, ModHistorySource.Catalog], morning.Sources);
        Assert.Null(sessions[0].LaunchedAt); // после удаления игру ещё не запускали
        Assert.Single(ModHistory.ReadEntries(_file), e => e.IsLaunch);
    }

    [Fact]
    public void WithoutLaunch_ABreakOfMoreThanHalfAnHour_StartsANewSession()
    {
        ModHistory.Record(_file, "a", null, "1", "2", T0);
        ModHistory.Record(_file, "b", null, "1", "2", T0.AddMinutes(29));
        ModHistory.Record(_file, "c", null, "1", "2", T0.AddMinutes(61));

        var sessions = ModHistory.Read(_file, T0.AddHours(2));
        Assert.Equal([["c"], ["a", "b"]], sessions.Select(s => s.Changes.Select(c => c.ModId).ToList()));
    }

    [Fact]
    public void Net_ShowsVersionBeforeAndAfterTheSession_PerMod()
    {
        ModHistory.Record(_file, "carryon", "Carry On", "1.0", "1.1", T0);
        ModHistory.Record(_file, "carryon", "Carry On", "1.1", "1.2", T0.AddMinutes(1));
        ModHistory.Record(_file, "temp", "Temp", null, "0.1", T0.AddMinutes(2));
        ModHistory.Record(_file, "temp", "Temp", "0.1", null, T0.AddMinutes(3));     // поставили и тут же убрали
        ModHistory.Record(_file, "back", "Back", "3.0", "2.0", T0.AddMinutes(4));
        ModHistory.Record(_file, "back", "Back", "2.0", "3.0", T0.AddMinutes(5));    // вернули как было

        // поставили и убрали, вернули как было — итога нет
        var net = Assert.Single(ModHistory.Read(_file, T0.AddHours(1))).Net;
        Assert.Equal([("carryon", (string?)"1.0", (string?)"1.2")], net.Select(c => (c.ModId, c.From, c.To)));
    }

    [Fact]
    public void Keeps30Days_ButAlwaysTheLastThreeSessions()
    {
        var now = T0.AddDays(100);
        ModHistory.Record(_file, "s1", null, "1", "2", T0);
        ModHistory.Record(_file, "s2", null, "1", "2", T0.AddDays(1));
        ModHistory.Record(_file, "s3", null, "1", "2", T0.AddDays(2));
        ModHistory.Record(_file, "s4", null, "1", "2", T0.AddDays(3));
        // месяц в отпуске — всё старше срока, но последние три сеанса на месте
        Assert.Equal(["s4", "s3", "s2"], ModHistory.Read(_file, now).Select(s => s.Changes.Single().ModId));

        ModHistory.Record(_file, "fresh", null, "1", "2", now.AddDays(-1));
        ModHistory.Record(_file, "fresh2", null, "1", "2", now);
        Assert.Equal(["fresh2", "fresh", "s4"], ModHistory.Read(_file, now).Select(s => s.Changes.Single().ModId));
    }

    [Fact]
    public void NestedBegin_StaysInTheOuterPress_AndScopeEndsWithIt()
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
    public async Task PressFlowsIntoAwaitedWork_AndIsJoinedByAnotherProcess()
    {
        string id;
        using (ModHistory.Begin(ModHistorySource.Rollback, undoes: "abc123"))
        {
            id = ModHistory.Current!.Id;
            await Task.Run(() => ModHistory.Record(_file, "a", "A", "2", "1"));
        }
        using (ModHistory.Join(id, ModHistorySource.Rollback, "abc123"))
            ModHistory.Record(_file, "b", "B", "3", "2");

        var entries = ModHistory.ReadEntries(_file);
        Assert.All(entries, e => Assert.Equal((id, ModHistorySource.Rollback, "abc123"), (e.Op, e.Source, e.Undoes)));
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
    public void BrokenLines_AreSkipped_TimesAreUtc()
    {
        ModHistory.Record(_file, "first", null, "1", "2", T0);
        File.AppendAllText(_file, "{ это не json\n");
        ModHistory.Record(_file, "second", null, "1", "2", T0.AddMinutes(1));

        var session = Assert.Single(ModHistory.Read(_file, T0.AddHours(1)));
        Assert.Equal(["first", "second"], session.Changes.Select(c => c.ModId));
        Assert.Equal(DateTimeKind.Utc, session.Start.Kind);
    }

    [Fact]
    public void LongGoneEntries_ArePrunedFromTheFile()
    {
        ModHistory.Record(_file, "s1", null, "1", "2", DateTime.UtcNow.AddDays(-200));
        ModHistory.Record(_file, "s2", null, "1", "2", DateTime.UtcNow.AddDays(-150));
        ModHistory.Record(_file, "s3", null, "1", "2", DateTime.UtcNow.AddDays(-120));
        ModHistory.Record(_file, "s4", null, "1", "2", DateTime.UtcNow.AddDays(-100));
        ModHistory.Record(_file, "fresh", null, "1", "2");
        Assert.Equal(["s3", "s4", "fresh"], ModHistory.ReadEntries(_file).Select(e => e.ModId));
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

        var session = Assert.Single(ModHistory.Read(_file));
        Assert.Equal([ModHistorySource.UpdateAll], session.Sources);
        Assert.Equal([("carryon", "1.0.0", "1.1.0"), ("newmod", null, "2.0.0")],
            session.Changes.Select(c => (c.ModId, c.From, c.To)));
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

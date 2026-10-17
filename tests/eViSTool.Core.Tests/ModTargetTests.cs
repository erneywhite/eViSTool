using System.IO.Compression;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using Newtonsoft.Json;

namespace eViSTool.Core.Tests;

/// <summary>Операция с модами работает со своей целью, а не с тем профилем, который активен сейчас (аудит, пункт 1).</summary>
public sealed class ModTargetTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-target-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private readonly List<string> _ids = [];

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        foreach (var id in _ids) // хранилище прежних версий — в папке данных тестового прогона, под id профиля
            if (Directory.Exists(Path.Combine(AppPaths.ModBackups, id))) Directory.Delete(Path.Combine(AppPaths.ModBackups, id), recursive: true);
    }

    private GameProfile Server(string name, params string[] disabled)
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var mods = Directory.CreateDirectory(Path.Combine(data, "Mods")).FullName;
        File.WriteAllText(Path.Combine(data, "serverconfig.json"),
            JsonConvert.SerializeObject(new { ModPaths = new[] { "Mods", mods }, WorldConfig = new { DisabledMods = disabled } }));
        _ids.Add($"{name}-{_run}");
        return new GameProfile { Id = $"{name}-{_run}", Name = name, Kind = ProfileKind.Server, DataDir = data };
    }

    private string Zip(string fileName, string modId, string version)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "dl")).FullName;
        var path = Path.Combine(dir, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open());
        w.Write($$"""{ "modid": "{{modId}}", "name": "{{modId}}", "version": "{{version}}" }""");
        return path;
    }

    private static string[] ModsOf(GameProfile p) => Directory.GetFiles(Path.Combine(p.DataDir!, "Mods")).Select(Path.GetFileName).ToArray()!;

    [Fact]
    public async Task EachTargetGetsItsOwnMod()
    {
        var a = Server("A");
        var b = Server("B");
        var targetA = ModTarget.For(a)!;
        var targetB = ModTarget.For(b)!; // «переключились» на B, пока для A шла загрузка

        var forB = await ModTargets.InstallAsync(targetB, Zip("beta.zip", "beta", "1.0.0"));
        var forA = await ModTargets.InstallAsync(targetA, Zip("alpha.zip", "alpha", "1.0.0"));

        Assert.Equal(["alpha.zip"], ModsOf(a));
        Assert.Equal(["beta.zip"], ModsOf(b));
        Assert.Equal("beta 1.0.0", forB!.Text);
        Assert.Equal("alpha 1.0.0", forA!.Text);
    }

    [Fact]
    public async Task TargetIsASnapshot_EditingTheProfileLaterDoesNotMoveIt()
    {
        var a = Server("A");
        var target = ModTarget.For(a)!;
        var elsewhere = Server("Elsewhere");
        a.DataDir = elsewhere.DataDir; // профиль поправили в настройках, пока задание ждало в очереди

        await ModTargets.InstallAsync(target, Zip("alpha.zip", "alpha", "1.0.0"));

        Assert.Equal(["alpha.zip"], Directory.GetFiles(Path.Combine(_root, "A", "Mods")).Select(Path.GetFileName));
        Assert.Empty(ModsOf(elsewhere));
    }

    [Fact]
    public async Task Declined_ChangesNothing()
    {
        var a = Server("A");
        await ModTargets.InstallAsync(ModTarget.For(a)!, Zip("alpha_1.0.0.zip", "alpha", "1.0.0"));

        InstallPlan? seen = null;
        var outcome = await ModTargets.InstallAsync(ModTarget.For(a)!, Zip("alpha_0.9.0.zip", "alpha", "0.9.0"), plan => { seen = plan; return false; });

        Assert.Null(outcome);
        Assert.True(seen!.IsDowngrade);
        Assert.Equal(["alpha_1.0.0.zip"], ModsOf(a));
    }

    [Fact]
    public async Task Update_KeepsADisabledModDisabled_AndTheOldVersionForRollback()
    {
        var a = Server("A", "alpha");
        await ModTargets.InstallAsync(ModTarget.For(a)!, Zip("alpha_1.0.0.zip", "alpha", "1.0.0"));

        var outcome = await ModTargets.InstallAsync(ModTarget.For(a)!, Zip("alpha_1.1.0.zip", "alpha", "1.1.0"));

        Assert.Equal("alpha: 1.0.0 → 1.1.0", outcome!.Text);
        Assert.Equal(["alpha_1.1.0.zip"], ModsOf(a));
        Assert.Contains("alpha", ProfileResolver.Resolve(a).DisabledMods);
        Assert.Single(ModBackupStore.ForProfile(a).List("alpha"));
    }

    [Fact]
    public async Task Delete_Local_GoesToTrash_WritesHistory_AndForgetsTheLastCopy()
    {
        var a = Server("A", "alpha", "other");
        await ModTargets.InstallAsync(ModTarget.For(a)!, Zip("alpha_1.0.0.zip", "alpha", "1.0.0"));
        var (_, mods) = await ModTargets.ScanAsync(ModTarget.For(a)!);
        var trashed = new List<string>();
        try
        {
            await ModTargets.DeleteAsync(ModTarget.For(a)!, mods.Single(), path => { trashed.Add(path); File.Delete(path); });

            Assert.Equal([Path.Combine(a.DataDir!, "Mods", "alpha_1.0.0.zip")], trashed);
            Assert.Empty(ModsOf(a));
            Assert.Equal(["other"], ProfileResolver.Resolve(a).DisabledMods);
            var last = ModHistory.ReadEntries(ModHistory.FileFor(a.Id))[^1];
            Assert.Equal(("alpha", "1.0.0", (string?)null), (last.ModId, last.From, last.To));
        }
        finally
        {
            File.Delete(ModHistory.FileFor(a.Id));
        }
    }

    [Fact]
    public async Task Delete_Local_AnotherCopyLeft_KeepsTheDisabledEntry()
    {
        var a = Server("A", "alpha");
        var mods = Path.Combine(a.DataDir!, "Mods");
        File.Copy(Zip("alpha_1.0.0.zip", "alpha", "1.0.0"), Path.Combine(mods, "alpha_1.0.0.zip"));
        File.Copy(Zip("alpha_copy.zip", "alpha", "1.0.0"), Path.Combine(mods, "alpha_copy.zip"));
        var (_, list) = await ModTargets.ScanAsync(ModTarget.For(a)!);
        try
        {
            await ModTargets.DeleteAsync(ModTarget.For(a)!, list.First(), File.Delete);

            Assert.Single(ModsOf(a));
            Assert.Equal(["alpha"], ProfileResolver.Resolve(a).DisabledMods);
        }
        finally
        {
            File.Delete(ModHistory.FileFor(a.Id));
        }
    }
}

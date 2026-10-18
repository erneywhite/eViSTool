using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using Newtonsoft.Json;

namespace eViSTool.Core.Tests.Hosting;

public sealed class HostedBackupFoldersTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "evistool-bkf-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_base, "HostedBackups");

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (IOException) { }
    }

    private static GameProfile Hosted(string name, string? folder = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        Kind = ProfileKind.Server,
        RemoteCode = GameProfile.HostedMarker,
        Hosted = new HostedConnection { Host = "h.test", BackupFolder = folder },
    };

    [Fact]
    public void NameComesFromTheProfile()
    {
        var p = Hosted("Мой сервер");
        Assert.True(HostedBackupFolders.Assign([p], Root));
        Assert.Equal("Мой сервер", p.Hosted!.BackupFolder);
        Assert.False(HostedBackupFolders.Assign([p], Root)); // второй раз ничего не меняется
    }

    [Theory]
    [InlineData("a<b>c:d\"e/f\\g|h?i*j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("  name.  ", "name")]
    [InlineData("", "Server")]
    [InlineData("...", "Server")]
    [InlineData("CON", "CON_")]
    [InlineData("lpt1", "lpt1_")]
    [InlineData("nul.txt", "nul.txt_")]
    [InlineData("Console", "Console")]
    public void Sanitize_ReplacesWhatTheFileSystemRejects(string name, string expected) =>
        Assert.Equal(expected, HostedBackupFolders.Sanitize(name));

    [Fact]
    public void Sanitize_CutsLongNames() =>
        Assert.Equal(60, HostedBackupFolders.Sanitize(new string('x', 200)).Length);

    [Fact]
    public void SameNames_GetNumbers_CaseInsensitive()
    {
        var a = Hosted("World");
        var b = Hosted("world");
        var c = Hosted("World");
        HostedBackupFolders.Assign([a, b, c], Root);
        Assert.Equal(["World", "world (2)", "World (3)"], new[] { a, b, c }.Select(p => p.Hosted!.BackupFolder));
    }

    [Fact]
    public void CloneWithTheSameFolder_GetsItsOwn()
    {
        var a = Hosted("Srv");
        HostedBackupFolders.Assign([a], Root);
        var clone = Hosted("Srv (копия)");
        clone.Hosted = a.Hosted!.Clone();
        HostedBackupFolders.Assign([a, clone], Root);
        Assert.Equal("Srv", a.Hosted!.BackupFolder);
        Assert.Equal("Srv (копия)", clone.Hosted!.BackupFolder);
    }

    [Fact]
    public void CloneWithSameName_GetsNumber()
    {
        var a = Hosted("Srv");
        HostedBackupFolders.Assign([a], Root);
        var clone = Hosted("Srv");
        clone.Hosted = a.Hosted!.Clone();
        HostedBackupFolders.Assign([a, clone], Root);
        Assert.Equal("Srv (2)", clone.Hosted!.BackupFolder);
    }

    [Fact]
    public void ForeignFolderOnDisk_IsNotTaken()
    {
        Directory.CreateDirectory(Path.Combine(Root, "srv"));
        var p = Hosted("Srv");
        HostedBackupFolders.Assign([p], Root);
        Assert.Equal("Srv (2)", p.Hosted!.BackupFolder);
    }

    [Fact]
    public void FolderOfALaterProfile_IsNotTakenByAnEarlierOne()
    {
        var early = Hosted("Srv");
        var late = Hosted("Other", "Srv");
        HostedBackupFolders.Assign([early, late], Root);
        Assert.Equal("Srv", late.Hosted!.BackupFolder);
        Assert.Equal("Srv (2)", early.Hosted!.BackupFolder);
    }

    [Fact]
    public void OldFolderById_IsRenamedToTheName()
    {
        var p = Hosted("Srv");
        var old = Path.Combine(Root, p.Id);
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "copy.vcdbs"), "x");
        Assert.True(HostedBackupFolders.Assign([p], Root));
        Assert.False(Directory.Exists(old));
        Assert.True(File.Exists(Path.Combine(Root, "Srv", "copy.vcdbs")));
        Assert.Equal("Srv", p.Hosted!.BackupFolder);
    }

    [Fact]
    public void OldFolderById_StaysWhenItCannotBeMoved()
    {
        var p = Hosted("Srv");
        var old = Path.Combine(Root, p.Id);
        Directory.CreateDirectory(old);
        // на месте будущего имени — файл: Directory.Move не получится, а как занятая по папкам оно не считается
        File.WriteAllText(Path.Combine(Root, "Srv"), "not a folder");
        HostedBackupFolders.Assign([p], Root);
        Assert.Equal(p.Id, p.Hosted!.BackupFolder);
        Assert.True(Directory.Exists(old));
    }

    [Fact]
    public void RenamingTheProfile_KeepsTheFolder()
    {
        var p = Hosted("Srv");
        HostedBackupFolders.Assign([p], Root);
        p.Name = "Совсем другое";
        Assert.False(HostedBackupFolders.Assign([p], Root));
        Assert.Equal("Srv", p.Hosted!.BackupFolder);
    }

    [Fact]
    public void BadFolderInSettings_IsReplaced()
    {
        var p = Hosted("Srv", "../elsewhere");
        Assert.True(HostedBackupFolders.Assign([p], Root));
        Assert.Equal("Srv", p.Hosted!.BackupFolder);
    }

    [Fact]
    public void ProfilesWithoutHosting_AreIgnored() =>
        Assert.False(HostedBackupFolders.Assign([new GameProfile { Name = "Local" }], Root));

    [Fact]
    public void OwnBackupDir_BeatsTheDefaultFolder()
    {
        var own = Path.Combine(_base, "own");
        var p = Hosted("Srv");
        p.Hosted!.BackupDir = own;
        HostedBackupFolders.Assign([p], Root);
        Assert.Equal(own, new HostedWorldBackups(ModTarget.For(p)!).Dir);
    }

    [Fact]
    public void BackupsDir_FollowsTheAssignedFolder()
    {
        var p = Hosted("Srv");
        HostedBackupFolders.Assign([p], Root);
        Assert.Equal(HostedWorldBackups.DefaultDir(p.Id, "Srv"), new HostedWorldBackups(ModTarget.For(p)!).Dir);
    }

    [Fact]
    public void DefaultDir_UsesFolderOrFallsBackToId_AndStaysInside()
    {
        var root = Path.Combine(AppPaths.Root, "HostedBackups");
        Assert.Equal(Path.Combine(root, "Srv"), HostedWorldBackups.DefaultDir("id1", "Srv"));
        Assert.Equal(Path.Combine(root, "id1"), HostedWorldBackups.DefaultDir("id1"));
        Assert.Equal(Path.Combine(root, "id1"), HostedWorldBackups.DefaultDir("id1", "../x"));
        Assert.Throws<ArgumentException>(() => HostedWorldBackups.DefaultDir("../x", "..\\y"));
    }

    [Fact]
    public void BackupFolder_IsStoredInSettings_AndOmittedWhenEmpty()
    {
        var c = new HostedConnection { Host = "h", BackupFolder = "Srv" };
        Assert.Equal("Srv", JsonConvert.DeserializeObject<HostedConnection>(JsonConvert.SerializeObject(c))!.BackupFolder);
        Assert.DoesNotContain("BackupFolder", JsonConvert.SerializeObject(new HostedConnection { Host = "h" }));
    }
}

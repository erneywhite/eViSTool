using eViSTool.Core.Profiles;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests;

/// <summary>Кто ещё пользуется папкой профиля — перед тем как предложить её удалить (аудит, пункт 9).</summary>
public sealed class ProfileUsageTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-usage-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private GameProfile Server(string name, string[]? modPaths = null, string? save = null)
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, "ServerProfiles", name)).FullName;
        Directory.CreateDirectory(Path.Combine(data, "Mods"));
        File.WriteAllText(Path.Combine(data, "serverconfig.json"), new JObject
        {
            ["ModPaths"] = new JArray(modPaths ?? ["Mods", Path.Combine(data, "Mods")]),
            ["WorldConfig"] = new JObject { ["SaveFileLocation"] = save ?? Path.Combine(data, "Saves", "default.vcdbs") },
        }.ToString());
        return new GameProfile { Name = name, Kind = ProfileKind.Server, DataDir = data };
    }

    [Fact]
    public void SourceOfSharedMods_IsUsedByTheCloneThatSharesThem()
    {
        var a = Server("A");
        var b = Server("B", modPaths: ["Mods", Path.Combine(a.DataDir!, "Mods")]); // клон с общими модами

        var users = ProfileUsage.UsersOf(a.DataDir!, [b]);

        var use = Assert.Single(users);
        Assert.Equal(ProfileUseKind.Mods, use.Kind);
        Assert.Same(b, use.Profile);
        Assert.Contains("B", use.Describe());
    }

    [Fact]
    public void IndependentProfiles_DoNotUseEachOther()
    {
        var a = Server("A");
        var b = Server("B");
        Assert.Empty(ProfileUsage.UsersOf(a.DataDir!, [b]));
        Assert.Empty(ProfileUsage.UsersOf(b.DataDir!, [a]));
    }

    [Fact]
    public void AWorldStoredInTheFolder_IsAUse()
    {
        var a = Server("A");
        var b = Server("B", save: Path.Combine(a.DataDir!, "Saves", "b-world.vcdbs"));

        var use = Assert.Single(ProfileUsage.UsersOf(a.DataDir!, [b]));
        Assert.Equal(ProfileUseKind.World, use.Kind);
    }

    [Fact]
    public void ADataFolderInside_IsAUse()
    {
        var a = Server("A");
        var inner = new GameProfile { Name = "inner", Kind = ProfileKind.Server, DataDir = Directory.CreateDirectory(Path.Combine(a.DataDir!, "ServerProfiles", "inner")).FullName };

        Assert.Equal(ProfileUseKind.Data, Assert.Single(ProfileUsage.UsersOf(a.DataDir!, [inner])).Kind);
    }

    [Fact]
    public void SimilarNames_CaseAndTrailingSlash_AreHandled()
    {
        var a = Server("A");
        var ab = Server("AB", modPaths: ["Mods", Path.Combine(_root, "ServerProfiles", "AB", "Mods")]); // «AB» — не внутри «A»
        Assert.Empty(ProfileUsage.UsersOf(a.DataDir!, [ab]));

        var shout = Server("C", modPaths: ["Mods", Path.Combine(a.DataDir!, "Mods").ToUpperInvariant() + "\\"]);
        Assert.Single(ProfileUsage.UsersOf(a.DataDir! + "\\", [shout]));

        Assert.True(ProfileUsage.IsSameOrInside(a.DataDir!, a.DataDir!.ToLowerInvariant() + "/"));
        Assert.False(ProfileUsage.IsSameOrInside(ab.DataDir!, a.DataDir!));
    }

    [Fact]
    public void RemoteProfiles_AreIgnored()
    {
        var a = Server("A");
        var remote = new GameProfile { Name = "R", Kind = ProfileKind.Server, DataDir = a.DataDir, RemoteCode = "x" }; // путь на другой машине
        Assert.Empty(ProfileUsage.UsersOf(a.DataDir!, [remote]));
    }
}

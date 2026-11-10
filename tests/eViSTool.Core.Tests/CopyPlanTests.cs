using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.Core.Tests;

/// <summary>«Скопировать в профиль»: что ставится, а что нет (клиентское на сервер, папки, такое же или новее в цели).</summary>
public sealed class CopyPlanTests
{
    private static LocalMod Mod(string id, string version, string? side = null, string? path = null) =>
        new(path ?? $"/mods/{id}_{version}.zip", new ModInfo { ModId = id, Name = id, Version = version, Side = side }, null);

    [Fact]
    public void Installs_MissingAndOlder_LeavesTheSameAndNewer()
    {
        var ours = new[] { Mod("fresh", "1.0.0"), Mod("upd", "2.0.0"), Mod("same", "1.0.0"), Mod("down", "1.0.0"), Mod("odd", "beta") };
        var there = new[] { Mod("upd", "1.5.0"), Mod("same", "1.0.0"), Mod("DOWN", "1.2.0"), Mod("odd", "alpha") };

        var plan = CopyPlan.For(ours, there, ProfileKind.Client, _ => false);

        Assert.Equal([("fresh", null), ("upd", "1.5.0")], plan.Install.Select(i => (i.Mod.Info!.ModId, i.Replaces)));
        // такое же, новее и неразбираемая версия — не трогаем: лучше не поставить, чем молча понизить
        Assert.Equal(["same", "down", "odd"], plan.NotOlder.Select(n => n.Mod.Info!.ModId));
        Assert.Equal("1.2.0", plan.NotOlder[1].Theirs);
    }

    [Fact]
    public void Server_SkipsClientOnly_AndFoldersAreNeverCopied()
    {
        var hud = Mod("hud", "1.0.0", side: "Client");
        var folder = Mod("dev", "0.1.0", path: "/mods/dev");
        var plan = CopyPlan.For([hud, folder, Mod("core", "1.0.0", side: "Universal")], [], ProfileKind.Server, m => m == folder);

        Assert.Equal(["core"], plan.Install.Select(i => i.Mod.Info!.ModId));
        Assert.Same(hud, Assert.Single(plan.ClientOnly));
        Assert.Same(folder, Assert.Single(plan.Folders));
        Assert.Equal(2, plan.Notes(m => m.Info!.Name).Count);

        // в клиентский профиль клиентский мод ставится
        Assert.Contains(CopyPlan.For([hud], [], ProfileKind.Client, _ => false).Install, i => i.Mod == hud);
    }
}

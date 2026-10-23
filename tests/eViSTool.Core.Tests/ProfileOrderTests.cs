using eViSTool.Core.Profiles;

namespace eViSTool.Core.Tests;

/// <summary>Порядок профилей в «Профилях» и в шапке.</summary>
public sealed class ProfileOrderTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-order-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static List<string> Sorted(IEnumerable<GameProfile> profiles, Comparison<GameProfile>? compare)
    {
        var list = profiles.ToList();
        if (compare is not null) list = [.. list.Order(Comparer<GameProfile>.Create(compare))];
        return [.. list.Select(p => p.Name)];
    }

    [Fact]
    public void LastPlayed_RecentFirst_NeverPlayedLastInCreationOrder()
    {
        var now = new DateTime(2026, 10, 23, 12, 0, 0);
        GameProfile[] profiles =
        [
            new() { Name = "Main" },
            new() { Name = "Old", LastPlayed = now.AddDays(-3) },
            new() { Name = "Fresh", LastPlayed = now },
            new() { Name = "Unused" },
        ];

        // OrderBy устойчивый — как FilteredList: равные (оба «ни разу») остаются в порядке создания
        Assert.Equal(["Fresh", "Old", "Main", "Unused"], Sorted(profiles, ProfileOrder.For(ProfileSort.LastPlayed, p => p.LastPlayed)));
    }

    [Fact]
    public void Name_IgnoresCase_Created_KeepsOrder()
    {
        GameProfile[] profiles = [new() { Name = "beta" }, new() { Name = "Alpha" }, new() { Name = "Gamma" }];

        Assert.Equal(["Alpha", "beta", "Gamma"], Sorted(profiles, ProfileOrder.For(ProfileSort.Name, _ => null)));
        Assert.Null(ProfileOrder.For(ProfileSort.Created, _ => null));
    }

    [Fact]
    public void LastPlayed_TakesTheGameLogWhenItIsLater()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_root, "Logs")).FullName;
        var log = Path.Combine(logs, "client-main.log");
        File.WriteAllText(log, "");
        var written = new DateTime(2026, 10, 20, 18, 30, 0);
        File.SetLastWriteTime(log, written);

        // играли с ярлыка после последнего запуска из окна — берём лог
        var profile = new GameProfile { Kind = ProfileKind.Client, DataDir = _root, LastPlayed = written.AddDays(-1) };
        Assert.Equal(written, ProfileOrder.LastPlayed(profile));

        // запуск из окна позже лога (игра ещё грузится) — берём отметку окна
        profile.LastPlayed = written.AddHours(1);
        Assert.Equal(written.AddHours(1), ProfileOrder.LastPlayed(profile));

        // у сервера — свой лог: клиентский не в счёт
        Assert.Null(ProfileOrder.LastPlayed(new GameProfile { Kind = ProfileKind.Server, DataDir = _root }));
    }
}

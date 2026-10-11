using eViSTool.Core.AppUpdate;
using eViSTool.Core.Versioning;

namespace eViSTool.Core.Tests;

/// <summary>Список изменений для окна «Вышла новая версия» — из текста релиза на GitHub (образец — релиз 0.10.2).</summary>
public sealed class ReleaseNotesTests
{
    private const string Body = """
        **eViSTool 0.10.2: mod history with rollback.**

        **New**
        - Mod history: the History tab in My mods.
        - Announcements can go out at set times.

        **Fixes**
        - Commands now go **one at a time**.

        Update from About; then update the agent on the server computer with Update on the server.

        On ModDB: [eViSTool — Windows](https://mods.vintagestory.at/evistoolwin) and [eViSTool — Linux](https://mods.vintagestory.at/evistoollinux).

        ---

        История модов с откатом.

        **Новое**
        - История модов: вкладка «История» в «Моих модах».
        - Объявления можно отправлять в заданное время.

        **Исправления**
        - Команды уходят по одной, см. [страницу](https://example.org).

        Обнови из «О программе», потом обнови агента на компьютере с сервером кнопкой «Обновить на сервере».
        """;

    [Fact]
    public void English_TakesTheFirstPart_WithoutProductPrefixAndTail()
    {
        var notes = ReleaseNotes.Parse(Body, russian: false);

        Assert.Equal("Mod history with rollback", notes.Summary);
        Assert.Equal(["New", "Fixes"], notes.Sections.Select(s => s.Heading));
        Assert.Equal(["Mod history: the History tab in My mods.", "Announcements can go out at set times."], notes.Sections[0].Items);
        Assert.Equal(["Commands now go one at a time."], notes.Sections[1].Items); // без «**» и без «Update from About…»
    }

    [Fact]
    public void Russian_TakesThePartAfterTheRule()
    {
        var notes = ReleaseNotes.Parse(Body.Replace("\n", "\r\n"), russian: true);

        Assert.Equal("История модов с откатом", notes.Summary);
        Assert.Equal(["Новое", "Исправления"], notes.Sections.Select(s => s.Heading));
        Assert.Equal("Команды уходят по одной, см. страницу.", Assert.Single(notes.Sections[1].Items));
    }

    [Fact]
    public void ItemsWithoutHeading_AndNoRussianPart()
    {
        var notes = ReleaseNotes.Parse("**eViSTool 0.10.3: reliability fixes.**\n\n- One.\n- Two.\n", russian: true);

        Assert.Equal("Reliability fixes", notes.Summary);
        var section = Assert.Single(notes.Sections);
        Assert.Null(section.Heading);
        Assert.Equal(["One.", "Two."], section.Items);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoBody_IsEmpty(string? body) => Assert.True(ReleaseNotes.Parse(body, russian: false).IsEmpty);

    [Fact]
    public void NewerReleases_AllSkippedVersions_NewestFirst()
    {
        static AppRelease R(string v) => new()
        {
            Version = ModVersion.ParseOrNull(v)!, Tag = "v" + v, AssetName = "a", AssetUrl = "u",
            Prerelease = ModVersion.ParseOrNull(v)!.IsPrerelease,
        };
        var all = new[] { R("0.10.1"), R("0.10.3"), R("0.11.0-beta.1"), R("0.10.2"), R("0.10.0") };

        Assert.Equal(["0.10.3", "0.10.2"], AppUpdater.NewerReleases(all, ModVersion.ParseOrNull("0.10.1")!).Select(r => r.Version.ToString()));
        Assert.Empty(AppUpdater.NewerReleases(all, ModVersion.ParseOrNull("0.10.3")!));
    }

    [Fact]
    public void ParseReleases_ReadsThePublishDate()
    {
        var json = $$"""
            [ { "tag_name": "v0.10.3", "published_at": "2026-10-11T01:15:26Z", "body": "x",
                "assets": [ { "name": "eViSTool-0.10.3{{AppUpdater.AssetSuffix}}", "browser_download_url": "u" } ] } ]
            """;
        var release = Assert.Single(AppUpdater.ParseReleases(json));
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 1, 15, 26, TimeSpan.Zero), release.Published);
    }
}

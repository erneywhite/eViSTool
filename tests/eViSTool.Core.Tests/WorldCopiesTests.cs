using eViSTool.Core.Profiles;

namespace eViSTool.Core.Tests;

public sealed class WorldCopiesTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "evistool-wcopy-" + Guid.NewGuid().ToString("N"));
    private readonly string _saves;

    public WorldCopiesTests()
    {
        _saves = Directory.CreateDirectory(Path.Combine(_data, "Saves")).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); } catch (IOException) { }
    }

    /// <summary>«Мир» — просто файл с содержимым; время изменения задаём явно (как будто в него играли тогда).</summary>
    private string World(string name, string content, DateTime changedUtc)
    {
        var path = Path.Combine(_saves, name + ".vcdbs");
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, changedUtc);
        return path;
    }

    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstRefresh_CopiesEveryWorld()
    {
        World("Alpha", "a1", T0);
        World("Beta", "b1", T0);

        var (copied, problems) = WorldCopies.Refresh(_data);

        Assert.Equal(["Alpha", "Beta"], copied);
        Assert.Empty(problems);
        Assert.Equal("a1", File.ReadAllText(Path.Combine(WorldCopies.DirFor(_data), "Alpha.vcdbs")));
        Assert.Equal(2, WorldCopies.List(_data).Count);
    }

    [Fact]
    public void Refresh_TouchesOnlyWorldsThatChangedSinceTheCopy()
    {
        World("Alpha", "a1", T0);
        World("Beta", "b1", T0);
        WorldCopies.Refresh(_data);

        // в Alpha поиграли, Beta не открывали
        World("Alpha", "a2", T0.AddHours(2));
        var (copied, _) = WorldCopies.Refresh(_data);

        Assert.Equal(["Alpha"], copied);
        Assert.Equal("a2", File.ReadAllText(Path.Combine(WorldCopies.DirFor(_data), "Alpha.vcdbs")));

        // ничего не менялось — копии не трогаются
        Assert.Empty(WorldCopies.Refresh(_data).Copied);
    }

    [Fact]
    public void Refresh_SeesChangesThatAreStillInTheJournal()
    {
        var alpha = World("Alpha", "a1", T0);
        WorldCopies.Refresh(_data);
        // игра пишет сначала в журнал: сам файл мира ещё старый
        File.WriteAllText(alpha + "-wal", "");
        File.SetLastWriteTimeUtc(alpha + "-wal", T0.AddHours(1));

        Assert.True(WorldCopies.NeedsCopy(alpha, Path.Combine(WorldCopies.DirFor(_data), "Alpha.vcdbs")));
    }

    [Fact]
    public void NoSpace_KeepsThePreviousCopy_AndReportsTheWorld()
    {
        World("Alpha", "a1", T0);
        WorldCopies.Refresh(_data);
        World("Alpha", "a2", T0.AddHours(1));

        var (copied, problems) = WorldCopies.Refresh(_data, freeSpace: _ => 0);

        Assert.Empty(copied);
        Assert.Single(problems);
        Assert.Contains("Alpha", problems[0]);
        Assert.Equal("a1", File.ReadAllText(Path.Combine(WorldCopies.DirFor(_data), "Alpha.vcdbs")));
        Assert.False(File.Exists(Path.Combine(WorldCopies.DirFor(_data), "Alpha.vcdbs.new")));
    }

    [Fact]
    public void Restore_PutsTheCopyBack_AndKeepsTheCurrentWorldAside()
    {
        var alpha = World("Alpha", "good", T0);
        WorldCopies.Refresh(_data);
        World("Alpha", "broken by a mod", T0.AddHours(1));

        var copy = WorldCopies.List(_data).Single(c => !c.IsBeforeRestore);
        WorldCopies.Restore(_data, copy);

        Assert.Equal("good", File.ReadAllText(alpha));
        var aside = WorldCopies.List(_data).Single(c => c.IsBeforeRestore);
        Assert.Equal("Alpha", aside.WorldName);
        Assert.Equal("broken by a mod", File.ReadAllText(aside.CopyPath));
        // мир теперь равен копии — новая копия не нужна, пока в него не поиграют
        Assert.Empty(WorldCopies.Refresh(_data).Copied);

        // передумал — вернуть отложенный
        WorldCopies.Restore(_data, aside);
        Assert.Equal("broken by a mod", File.ReadAllText(alpha));
    }

    [Fact]
    public void NoSaves_NothingToDo()
    {
        Directory.Delete(_saves);
        Assert.Empty(WorldCopies.Refresh(_data).Copied);
        Assert.Empty(WorldCopies.List(_data));
    }
}

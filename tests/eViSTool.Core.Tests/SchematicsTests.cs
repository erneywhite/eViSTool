using eViSTool.Core.Profiles;

namespace eViSTool.Core.Tests;

/// <summary>
/// Постройки WorldEdit: схемы из папки WorldEdit одного профиля переезжают в такую же папку другого (там их ставит
/// «/we import»). Имя схемы может прийти по сети — выйти им из папки нельзя.
/// </summary>
public sealed class SchematicsTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-schem-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Data(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    [Fact]
    public void List_OnlyJsonSchematics_FreshFirst_NoFolderMeansEmpty()
    {
        var data = Data("client");
        Assert.Empty(Schematics.List(data)); // ничего не экспортировали — папки нет

        var dir = Directory.CreateDirectory(Schematics.DirFor(data)).FullName;
        File.WriteAllText(Path.Combine(dir, "old.json"), "{}");
        File.SetLastWriteTimeUtc(Path.Combine(dir, "old.json"), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(Path.Combine(dir, "house.json"), "{\"blocks\":[]}");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "not a schematic");
        File.WriteAllText(Path.Combine(dir, "half.json.evistool.tmp"), "{");

        var list = Schematics.List(data);
        Assert.Equal(["house.json", "old.json"], list.Select(e => e.Name));
        Assert.Equal(13, list[0].Size);
    }

    [Theory]
    [InlineData("../serverconfig.json")]
    [InlineData("..\\serverconfig.json")]
    [InlineData("sub/house.json")]
    [InlineData("house.txt")]
    [InlineData(".json")]
    [InlineData("")]
    public void Names_CannotLeaveTheFolder(string name)
    {
        var data = Data("server");
        Assert.Throws<InvalidDataException>(() => Schematics.Write(data, name, [1]));
        Assert.Throws<InvalidDataException>(() => Schematics.Read(data, name));
        Assert.False(File.Exists(Path.Combine(data, "serverconfig.json")));
    }

    [Fact]
    public async Task Copy_MovesTheFileIntoTheOtherProfile_AndReplacesTheSameName()
    {
        var server = new LocalSchematicSource(Data("server"));
        var solo = new LocalSchematicSource(Data("solo"));
        await server.WriteAsync("dom.json", "{\"v\":1}"u8.ToArray());

        await SchematicSources.CopyAsync(server, solo, "dom.json");
        Assert.Equal("{\"v\":1}", File.ReadAllText(Path.Combine(solo.LocalFolder!, "dom.json")));

        await server.WriteAsync("dom.json", "{\"v\":2}"u8.ToArray());
        await SchematicSources.CopyAsync(server, solo, "dom.json");
        Assert.Equal("{\"v\":2}", File.ReadAllText(Path.Combine(solo.LocalFolder!, "dom.json")));
        Assert.Single(await solo.ListAsync()); // временного файла не осталось
    }
}

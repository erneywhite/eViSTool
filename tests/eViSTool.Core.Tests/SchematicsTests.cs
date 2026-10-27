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

    [Theory]
    [InlineData("cat.libraries", "Utility", "Library")]  // библиотека с «Utility» — всё равно в библиотеки
    [InlineData("cat.map", "QoL", "map")]                 // регистр не важен, общий «QoL» — после темы
    [InlineData("cat.interface", "QoL")]
    [InlineData(null, "Unicorns", "Fun")]                // ни одна тема не подошла
    [InlineData(null)]
    public void CategorySuggest_FirstMatchingThemeWins(string? expected, params string[] tags) =>
        Assert.Equal(expected, eViSTool.Core.Mods.ModCategorySuggest.ThemeFor(tags));

    [Fact]
    public void CategorySuggest_UntaggedLibraryByModId()
    {
        Assert.Equal("cat.libraries", eViSTool.Core.Mods.ModCategorySuggest.ThemeFor([], "jsonpatcheslib"));
        Assert.Equal("cat.map", eViSTool.Core.Mods.ModCategorySuggest.ThemeFor(["Map"], "maplib")); // теги важнее
        Assert.Null(eViSTool.Core.Mods.ModCategorySuggest.ThemeFor([""], "sheltered"));
    }

    [Theory]
    [InlineData("{\"SizeX\":3,\"BlockCodes\":{}}", true)]
    [InlineData("  {}  ", true)]
    [InlineData("[1,2]", false)]
    [InlineData("{} {}", false)]
    [InlineData("hello", false)]
    [InlineData("", false)]
    public void LooksLikeSchematic_OneJsonObject(string text, bool expected) =>
        Assert.Equal(expected, Schematics.LooksLikeSchematic(System.Text.Encoding.UTF8.GetBytes(text)));

    /// <summary>Как у настоящей схемы: размер, коды блоков и предметов (в сундуках) — моды по доменам, игра не в счёт.</summary>
    private const string House = """
        {"GameVersion":"1.22.7","SizeX":61,"SizeY":66,"SizeZ":72,
         "BlockCodes":{"1":"game:rock-andesite","2":"millwright:windmillrotor-north","3":"primitivesurvival:fishbasket-north"},
         "ItemCodes":{"7":"game:rot","8":"butchering:bloodportion","9":"creative:x"},
         "Indices":[0,1024,2048],"BlockIds":[1,1,2],"BlockEntities":{"22064130":"!<d@[F*O7u#64c"},"Entities":[],
         "OriginalPos":{"X":510619,"Z":516031}}
        """;

    [Fact]
    public void Info_SizeAndModsFromBlocksAndItems()
    {
        var info = SchematicInfo.Parse(System.Text.Encoding.UTF8.GetBytes(House))!;
        Assert.Equal((61, 66, 72), (info.SizeX, info.SizeY, info.SizeZ));
        Assert.Equal(["butchering", "millwright", "primitivesurvival"], info.Mods);
        Assert.Null(SchematicInfo.Parse("not json"u8.ToArray()));
        Assert.Null(SchematicInfo.Parse("[1,2]"u8.ToArray()));
    }

    [Fact]
    public void Info_MissingMods_CountsTurnedOffModsAsMissing()
    {
        var info = SchematicInfo.Parse(System.Text.Encoding.UTF8.GetBytes(House))!;
        static eViSTool.Core.Mods.LocalMod Mod(string id, bool off = false) =>
            new($"{id}.zip", new eViSTool.Core.Mods.ModInfo { ModId = id, Name = id }, null) { IsDisabled = off };

        Assert.Equal(["primitivesurvival"], info.MissingIn([Mod("MillWright"), Mod("butchering"), Mod("primitivesurvival", off: true)]));
        Assert.Empty(info.MissingIn([Mod("millwright"), Mod("butchering"), Mod("primitivesurvival")]));
        // форк держит прежний домен: блоки «millwright» даёт и millwrightfork
        Assert.Empty(info.MissingIn([Mod("millwrightfork"), Mod("butchering"), Mod("primitivesurvival")]));
    }

    [Fact]
    public void List_ShowsWhatIsInside()
    {
        var data = Data("client");
        Directory.CreateDirectory(Schematics.DirFor(data));
        File.WriteAllText(Path.Combine(Schematics.DirFor(data), "home1.json"), House);
        File.WriteAllText(Path.Combine(Schematics.DirFor(data), "broken.json"), "{");

        var list = Schematics.List(data).ToDictionary(e => e.Name);
        Assert.Equal(61, list["home1.json"].Info!.SizeX);
        Assert.Null(list["broken.json"].Info); // не разобрать — без сводки, но в списке
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

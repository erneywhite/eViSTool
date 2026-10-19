using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Versioning;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Откуда строка таблицы берёт значок мода (docs/design.md, раздел 7): файл — только с диска этой машины (отметка
/// сканера), логотип — из ответа модбазы, без её стандартной картинки.
/// </summary>
public sealed class ModIconSourceTests
{
    private static readonly ModFileStamp Stamp = new("/mods/carryon_1.8.0.zip", new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), 4096);

    private static ModRowViewModel Row(ModFileStamp? stamp, string? logo)
    {
        var info = new ModInfo { ModId = "carryon", OriginalModId = "carryon", Name = "Carry On", Version = "1.8.0" };
        var remote = new Dictionary<string, ModDbResult>
        {
            ["carryon"] = new(new ModDbMod { Name = "Carry On", LogoFile = logo }, null),
        };
        var local = new LocalMod("/mods/carryon_1.8.0.zip", info, null) { Stamp = stamp };
        var result = UpdateChecker.Evaluate([local], remote, ModVersion.ParseOrNull("1.22.7")!).Single();
        return new ModRowViewModel(result, [], false, ProfileKind.Client);
    }

    [Fact]
    public void Local_mod_gives_its_file_and_the_logo_as_fallback()
    {
        var row = Row(Stamp, "https://moddbcdn.vintagestory.at/carryon_1.png");
        Assert.Equal(new ModIconSource(Stamp, "https://moddbcdn.vintagestory.at/carryon_1.png"), row.Icon);
    }

    [Fact]
    public void Mod_off_this_machine_has_only_the_logo()
    {
        // удалённый сервер и сервер у хостера: сканер папки этих модов не читал — отметки нет
        Assert.Equal(new ModIconSource(null, "https://moddbcdn.vintagestory.at/carryon_1.png"),
            Row(null, "https://moddbcdn.vintagestory.at/carryon_1.png").Icon);
    }

    [Fact]
    public void ModDb_default_picture_is_no_logo()
    {
        Assert.Null(Row(Stamp, "https://moddbcdn.vintagestory.at/mod-default_a8110a8541b95c5592b109c5b569df79.png").Icon.Logo);
        Assert.Null(Row(Stamp, null).Icon.Logo);
    }

    [Fact]
    public void Rebuilt_row_of_the_same_file_has_an_equal_icon()
    {
        // таблица после операции строится заново: значение то же — окно не перечитывает иконку
        Assert.Equal(Row(Stamp, null).Icon, Row(Stamp with { }, null).Icon);
        Assert.NotEqual(Row(Stamp, null).Icon, Row(Stamp with { Length = 4097 }, null).Icon);
    }

    [Fact]
    public void Catalog_drops_the_default_picture_too()
    {
        var item = new ModDbListItem { Name = "Carry On", ModIdStrs = ["carryon"], Logo = "https://moddbcdn.vintagestory.at/mod-default_x.png" };
        Assert.Null(new CatalogItemViewModel(item, new Dictionary<string, string>()).Logo);
    }
}

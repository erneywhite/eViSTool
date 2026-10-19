using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Versioning;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Откуда строка таблицы берёт значок мода (docs/design.md, раздел 7): файл — только с диска этой машины (отметка
/// сканера), логотип — из ответа проверки обновлений, а до неё — тот, что узнала карточка мода; стандартная картинка
/// модбазы — не логотип.
/// </summary>
public sealed class ModIconSourceTests
{
    private static readonly ModFileStamp Stamp = new("/mods/carryon_1.8.0.zip", new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), 4096);

    private const string Logo = "https://moddbcdn.vintagestory.at/carryon_1.png";
    private const string DefaultPicture = "https://moddbcdn.vintagestory.at/mod-default_a8110a8541b95c5592b109c5b569df79.png";

    /// <summary>Строка после проверки обновлений: модбаза ответила, логотип — <paramref name="logo"/>.</summary>
    private static ModRowViewModel Row(ModFileStamp? stamp, string? logo, string? knownLogo = null) =>
        Build(stamp, new Dictionary<string, ModDbResult>
        {
            ["carryon"] = new(new ModDbMod { Name = "Carry On", LogoFile = logo }, null),
        }, knownLogo);

    /// <summary>Строка до проверки; <paramref name="knownLogo"/> — логотип, который узнала карточка мода.</summary>
    private static ModRowViewModel Unchecked(ModFileStamp? stamp, string? knownLogo = null) => Build(stamp, new Dictionary<string, ModDbResult>(), knownLogo);

    private static ModRowViewModel Build(ModFileStamp? stamp, Dictionary<string, ModDbResult> remote, string? knownLogo)
    {
        var info = new ModInfo { ModId = "carryon", OriginalModId = "carryon", Name = "Carry On", Version = "1.8.0" };
        var local = new LocalMod("/mods/carryon_1.8.0.zip", info, null) { Stamp = stamp };
        var result = UpdateChecker.Evaluate([local], remote, ModVersion.ParseOrNull("1.22.7")!).Single();
        return new ModRowViewModel(result, [], false, ProfileKind.Client, knownLogo: knownLogo);
    }

    [Fact]
    public void Local_mod_gives_its_file_and_the_logo_as_fallback()
    {
        var row = Row(Stamp, Logo);
        Assert.Equal(new ModIconSource(Stamp, Logo), row.Icon);
    }

    [Fact]
    public void Mod_off_this_machine_has_only_the_logo()
    {
        // удалённый сервер и сервер у хостера: сканер папки этих модов не читал — отметки нет
        Assert.Equal(new ModIconSource(null, Logo), Row(null, Logo).Icon);
    }

    [Fact]
    public void ModDb_default_picture_is_no_logo()
    {
        Assert.Null(Row(Stamp, DefaultPicture).Icon.Logo);
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
    public void Logo_the_card_found_goes_into_the_unchecked_row()
    {
        // до проверки строка знает только файл; карточка сходила на модбазу — логотип встаёт и в строку
        var row = Unchecked(Stamp);
        Assert.Equal(new ModIconSource(Stamp, null), row.Icon);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        row.UseLogo(Logo);
        Assert.Equal(new ModIconSource(Stamp, Logo), row.Icon);
        Assert.Equal([nameof(ModRowViewModel.Icon)], changed);

        // таблицу построили заново — у новой строки тот же значок сразу, окно его не перечитывает
        Assert.Equal(row.Icon, Unchecked(Stamp, Logo).Icon);
    }

    [Fact]
    public void Logo_from_the_check_wins_and_the_default_picture_is_never_learned()
    {
        // ответ проверки свежее и полнее того, что когда-то узнала карточка
        var row = Row(Stamp, Logo, knownLogo: "https://moddbcdn.vintagestory.at/old.png");
        Assert.Equal(Logo, row.Icon.Logo);
        row.UseLogo("https://moddbcdn.vintagestory.at/old.png");
        Assert.Equal(Logo, row.Icon.Logo);

        var plain = Unchecked(Stamp, knownLogo: DefaultPicture);
        Assert.Null(plain.Icon.Logo);
        plain.UseLogo(DefaultPicture);
        plain.UseLogo(" ");
        Assert.Null(plain.Icon.Logo);
    }

    [Fact]
    public void Catalog_drops_the_default_picture_too()
    {
        var item = new ModDbListItem { Name = "Carry On", ModIdStrs = ["carryon"], Logo = "https://moddbcdn.vintagestory.at/mod-default_x.png" };
        Assert.Null(new CatalogItemViewModel(item, new Dictionary<string, string>()).Logo);
    }
}

using eViSTool.Core.Hosting;

namespace eViSTool.Core.Tests.Hosting;

public sealed class RemotePathTests
{
    [Theory]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("Mods", "/Mods")]
    [InlineData("/Mods/", "/Mods")]
    [InlineData("//data//Mods///x.zip", "/data/Mods/x.zip")]
    [InlineData("\\data\\Mods\\x.zip", "/data/Mods/x.zip")]
    [InlineData("/./data/./Mods", "/data/Mods")]
    [InlineData("/Mods/Auto Map Markers 5.0.3 - Vintage Story 1.22.zip", "/Mods/Auto Map Markers 5.0.3 - Vintage Story 1.22.zip")]
    [InlineData("/Моды/мод.zip", "/Моды/мод.zip")]
    public void Normalize_UsesForwardSlashes(string path, string expected) => Assert.Equal(expected, RemotePath.Normalize(path));

    [Theory]
    [InlineData("..")]
    [InlineData("/Mods/../serverconfig.json")]
    [InlineData("Mods\\..\\..\\etc")]
    [InlineData("/Mods/x.zip\r\nDELE /serverconfig.json")] // перевод строки стал бы второй командой FTP
    [InlineData("/Mods/x\0.zip")]
    public void Normalize_RejectsEscapesAndControlCharacters(string path) =>
        Assert.Throws<InvalidDataException>(() => RemotePath.Normalize(path));

    [Fact]
    public void Combine_StaysInsideTheFolder()
    {
        Assert.Equal("/Mods", RemotePath.Combine("/", "Mods"));
        Assert.Equal("/g123456/Mods", RemotePath.Combine("/g123456", "Mods"));
        Assert.Equal("/g123456/Mods", RemotePath.Combine("/g123456/", "/Mods")); // ведущий «/» не уводит из корня
        Assert.Equal("/g123456", RemotePath.Combine("/g123456", "."));
        Assert.Equal("/data/Mods/x.zip", RemotePath.Combine("data\\Mods", "x.zip"));
        Assert.Throws<InvalidDataException>(() => RemotePath.Combine("/g123456", "../g999999"));
    }

    [Fact]
    public void ParentAndName()
    {
        Assert.Equal("/Mods", RemotePath.Parent("/Mods/x.zip"));
        Assert.Equal("/", RemotePath.Parent("/serverconfig.json"));
        Assert.Equal("/", RemotePath.Parent("/"));
        Assert.Equal("x.zip", RemotePath.Name("/Mods/x.zip"));
        Assert.Equal("Mods", RemotePath.Name("/Mods/"));
        Assert.Equal("", RemotePath.Name("/"));
    }

    [Fact]
    public void IsInside_IsCaseSensitive_AndNotFooledByPrefix()
    {
        Assert.True(RemotePath.IsInside("/", "/anything"));
        Assert.True(RemotePath.IsInside("/data", "/data"));
        Assert.True(RemotePath.IsInside("/data", "/data/Mods/x.zip"));
        Assert.False(RemotePath.IsInside("/data", "/database/x"));
        Assert.False(RemotePath.IsInside("/Mods", "/mods/x.zip")); // сервер на Linux: это другая папка
        Assert.False(RemotePath.IsInside("/data/Mods", "/data"));
    }

    [Fact]
    public void Relative_IsTheInverseOfCombine()
    {
        Assert.Equal("Mods", RemotePath.Relative("/", "/Mods"));
        Assert.Equal("data/serverconfig.json", RemotePath.Relative("/g1", "/g1/data/serverconfig.json"));
        Assert.Equal(".", RemotePath.Relative("/g1", "/g1/"));
        Assert.Equal("/g1/data/Mods", RemotePath.Combine("/g1", RemotePath.Relative("/g1", "/g1/data/Mods")));
        Assert.Throws<InvalidDataException>(() => RemotePath.Relative("/g1", "/g2/Mods"));
    }

    [Theory]
    [InlineData("carryon_1.8.0.zip", "carryon_1.8.0.zip")]
    [InlineData("Auto Map Markers 5.0.3 - Vintage Story 1.22.zip", "Auto Map Markers 5.0.3 - Vintage Story 1.22.zip")]
    [InlineData("мод: версия 1?.zip", "мод_ версия 1_.zip")]
    [InlineData("a<b>c|d\"e*f.zip", "a_b_c_d_e_f.zip")]
    [InlineData("tail. . ", "tail")]
    [InlineData("CON.zip", "_CON.zip")]
    [InlineData("com1", "_com1")]
    [InlineData("LPT9.txt", "_LPT9.txt")]
    [InlineData("COM10.zip", "COM10.zip")]
    [InlineData("..", "_")]
    [InlineData("", "_")]
    [InlineData("a/b\\c", "a_b_c")]
    public void SafeLocalName_WorksOnAnyOs(string name, string expected) => Assert.Equal(expected, RemotePath.SafeLocalName(name));
}

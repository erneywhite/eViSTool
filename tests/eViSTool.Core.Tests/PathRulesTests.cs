namespace eViSTool.Core.Tests;

/// <summary>Пути от окна и имена файлов — одинаково на Windows и Linux.</summary>
public sealed class PathRulesTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "evistool-paths", "data", "ModConfig");

    [Theory]
    [InlineData("a.json")]
    [InlineData("sub/b.json")]
    [InlineData("sub\\b.json")]          // окно на Windows: «\» — разделитель и на Linux
    [InlineData("sub/x/../b.json")]
    public void Inside_AcceptsPathsInTheFolder(string rel)
    {
        var full = PathRules.Inside(Root, rel);
        Assert.NotNull(full);
        Assert.StartsWith(Root + Path.DirectorySeparatorChar, full);
        Assert.Equal(Path.GetFileName(rel.Replace('\\', '/')), Path.GetFileName(full));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../secret.json")]
    [InlineData("..\\secret.json")]
    [InlineData("sub/../../secret.json")]
    [InlineData("sub\\..\\..\\secret.json")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\x.json")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("c:secret.json")]
    [InlineData("a\0.json")]
    public void Inside_RefusesEverythingOutside(string rel) => Assert.Null(PathRules.Inside(Root, rel));

    [Fact]
    public void Inside_SiblingInOtherCase_IsOutsideOnLinux()
    {
        // на Windows «modconfig» и «ModConfig» — одна папка, на Linux — соседняя, и туда нельзя
        var full = PathRules.Inside(Root, "../modconfig/a.json");
        if (OperatingSystem.IsWindows()) Assert.NotNull(full);
        else Assert.Null(full);
    }

    [Fact]
    public void BadFileNameChars_AreThoseOfWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            // на Windows — ровно прежний набор системы: имена копий и папок профилей там не меняются
            var system = Path.GetInvalidFileNameChars().ToHashSet();
            for (var c = '\0'; c < '\u0100'; c++) Assert.Equal(system.Contains(c), PathRules.IsBadInFileName(c));
        }
        Assert.All("\\/:*?\"<>|\t\0", c => Assert.True(PathRules.IsBadInFileName(c)));
        Assert.All("aZ09 .-_()[]Ёё", c => Assert.False(PathRules.IsBadInFileName(c)));
    }
}

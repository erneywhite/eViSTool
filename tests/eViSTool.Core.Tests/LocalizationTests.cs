using System.Text.RegularExpressions;
using eViSTool.Core.Localization;

namespace eViSTool.Core.Tests;

public partial class LocalizationTests
{
    [Fact]
    public void AllLanguagesHaveTheSameKeys()
    {
        var en = Loc.Keys("en").ToHashSet();
        Assert.NotEmpty(en);
        foreach (var (code, _) in Loc.Available)
        {
            var other = Loc.Keys(code).ToHashSet();
            Assert.True(en.SetEquals(other),
                $"{code}: нет {string.Join(", ", en.Except(other))}; лишние {string.Join(", ", other.Except(en))}");
        }
    }

    [Fact]
    public void EveryKeyUsedInCodeExists()
    {
        // ищем Loc.T("…") в коде и {local:Tr …} в разметке — все ключи должны быть в словаре
        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        Assert.True(Directory.Exists(src), src);

        var en = Loc.Keys("en").ToHashSet();
        var used = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => KeyUse().Matches(File.ReadAllText(f)).Select(m => m.Groups["k"].Value))
            .ToHashSet();

        Assert.NotEmpty(used);
        var missing = used.Except(en).ToList();
        Assert.True(missing.Count == 0, "нет в словаре: " + string.Join(", ", missing));
    }

    [Fact]
    public void FallsBackToEnglishAndFormats()
    {
        Loc.Instance.SetLanguage("ru");
        Assert.Equal("Модов: 5", Loc.T("mods.sumMods", 5));
        Loc.Instance.SetLanguage("xx"); // неизвестный язык — английский
        Assert.Equal("Mods: 5", Loc.T("mods.sumMods", 5));
        Assert.Equal("no.such.key", Loc.T("no.such.key"));
        Loc.Instance.SetLanguage("en");
    }

    [GeneratedRegex("""(?:Loc\.T\(\s*"|local:Tr\s+)(?<k>[a-zA-Z]+\.[a-zA-Z0-9.]+)""")]
    private static partial Regex KeyUse();
}

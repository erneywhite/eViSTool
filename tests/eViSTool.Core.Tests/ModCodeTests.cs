using eViSTool.Core.Packs;

namespace eViSTool.Core.Tests;

public sealed class ModCodeTests
{
    private static readonly ModCodeEntry[] Pack =
    [
        new(12228, 31), new(4567, 0), new(890, 182), new(12493, 2), new(3001, 17), new(7777, 5), new(150, 64),
    ];

    [Fact]
    public void RoundTrip_GivesTheSameSet_SortedByMod()
    {
        var code = ModCode.Encode(Pack);
        Assert.StartsWith(ModCode.Prefix, code);
        Assert.Equal(Pack.OrderBy(e => e.ModDbId), ModCode.Decode(code).Entries);
        Assert.Null(ModCode.Decode(code).GameVersion);
    }

    [Fact]
    public void GameVersion_TravelsWithTheCode()
    {
        var code = ModCode.Encode(Pack, Versioning.ModVersion.ParseOrNull("1.22.7"));
        Assert.Equal("1.22.7", ModCode.Decode(code).GameVersion?.ToString());
        Assert.Equal(7, ModCode.Decode(code).Entries.Count);
    }

    [Fact]
    public void ChatNoise_AroundTheCode_IsIgnored()
    {
        var code = ModCode.Encode(Pack);
        Assert.Equal(7, ModCode.Decode($"  `{code[..10]}\n{code[10..]}`  ").Entries.Count);
    }

    [Fact]
    public void NonBreakingHyphen_FromTheWindow_IsAccepted()
    {
        var code = ModCode.Encode(Pack);
        Assert.Equal(7, ModCode.Decode(code.Replace('-', '‑')).Entries.Count);
    }

    [Fact]
    public void SingleMod_AndZeroValues_Work()
    {
        Assert.Equal([new ModCodeEntry(0, 0)], ModCode.Decode(ModCode.Encode([new ModCodeEntry(0, 0)])).Entries);
        Assert.Equal([new ModCodeEntry(1, 0)], ModCode.Decode(ModCode.Encode([new ModCodeEntry(1, 0), new ModCodeEntry(1, 9)])).Entries);
    }

    [Fact]
    public void BigSet_IsAboutThreeCharsPerMod()
    {
        var rnd = new Random(7);
        var set = Enumerable.Range(0, 100).Select(_ => rnd.Next(1, 12500)).Distinct().Take(80)
            .Select(id => new ModCodeEntry(id, rnd.Next(0, 40))).ToList();
        var code = ModCode.Encode(set);
        Assert.Equal(set.OrderBy(e => e.ModDbId), ModCode.Decode(code).Entries);
        Assert.InRange((code.Length - ModCode.Prefix.Length) / (double)set.Count, 1.5, 3.5);
    }

    [Fact]
    public void AnyChangedCharacter_IsCaught()
    {
        var code = ModCode.Encode(Pack);
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        for (var i = ModCode.Prefix.Length; i < code.Length; i++)
        {
            var other = alphabet[(alphabet.IndexOf(code[i]) + 1) % alphabet.Length];
            var broken = code[..i] + other + code[(i + 1)..];
            Assert.Throws<FormatException>(() => ModCode.Decode(broken));
        }
        Assert.Throws<FormatException>(() => ModCode.Decode(code[..^1])); // потерялся последний символ
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("EV1-0OIl")]
    [InlineData("EV2-abc")]
    public void NotACode_IsRefused(string text) => Assert.Throws<FormatException>(() => ModCode.Decode(text));
}

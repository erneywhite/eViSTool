using eViSTool.Core.Profiles;
using Newtonsoft.Json;

namespace eViSTool.Core.Tests;

public sealed class ProfileIconTests
{
    [Fact]
    public void Names_FromList_Pass_Others_ReadAsUnset()
    {
        Assert.Equal("rust", ProfileIcon.Color("rust"));
        Assert.Equal("castle", ProfileIcon.Glyph("castle"));
        // имя из более новой версии окна, опечатка, пусто — «не выбрано»
        Assert.Null(ProfileIcon.Color("neon"));
        Assert.Null(ProfileIcon.Color("Rust"));
        Assert.Null(ProfileIcon.Color(""));
        Assert.Null(ProfileIcon.Glyph(null));
        Assert.Equal("server", ProfileIcon.Glyph("server")); // знак вида можно выбрать и профилю другого вида
    }

    /// <summary>Каждый цвет и знак назван на обоих языках — так их читает экранный диктор (ключи собираются из имени).</summary>
    [Fact]
    public void EveryColorAndSign_HasANameInEveryLanguage()
    {
        foreach (var lang in new[] { "ru", "en" })
        {
            var keys = Localization.Loc.Keys(lang).ToHashSet();
            var missing = ProfileIcon.Colors.Select(c => $"icon.color.{c}").Concat(ProfileIcon.Glyphs.Select(g => $"icon.glyph.{g}"))
                .Append("icon.color.none").Where(k => !keys.Contains(k)).ToList();
            Assert.True(missing.Count == 0, $"{lang}: нет {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void Unset_NotWritten_Set_RoundTrips()
    {
        var plain = JsonConvert.SerializeObject(new GameProfile { Name = "a" });
        Assert.DoesNotContain("IconColor", plain);
        Assert.DoesNotContain("IconGlyph", plain);

        var back = JsonConvert.DeserializeObject<GameProfile>(JsonConvert.SerializeObject(new GameProfile { IconColor = "sky", IconGlyph = "anchor" }))!;
        Assert.Equal("sky", back.IconColor);
        Assert.Equal("anchor", back.IconGlyph);
    }
}

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

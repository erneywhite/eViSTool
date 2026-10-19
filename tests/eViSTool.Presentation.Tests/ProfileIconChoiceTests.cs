using System.ComponentModel;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Выбор значка в карточке профиля. Ctrl+щелчок по выбранному образцу снимает выбор, и список пишет null: цвет и знак
/// при этом не сбрасываются и не сохраняются, а выделение возвращается на них.
/// </summary>
public sealed class ProfileIconChoiceTests
{
    [Fact]
    public void Deselecting_keeps_the_icon_and_puts_the_selection_back() => Sta.Run(() =>
    {
        var saved = 0;
        var profile = new ProfileViewModel(new GameProfile { Kind = ProfileKind.Client, IconColor = "rust", IconGlyph = "castle" },
            _ => saved++, _ => saved++);
        var renotified = new List<string?>();
        ((INotifyPropertyChanged)profile).PropertyChanged += (_, e) => renotified.Add(e.PropertyName);

        profile.ColorChoice = null;
        profile.GlyphChoice = null;
        Sta.WaitFor(() => renotified.Contains(nameof(ProfileViewModel.ColorChoice)) && renotified.Contains(nameof(ProfileViewModel.GlyphChoice)));

        Assert.Equal("rust", profile.IconColor);
        Assert.Equal("castle", profile.IconGlyph);
        Assert.True(profile.HasCustomIcon);
        Assert.Equal(0, saved);
    });

    [Fact]
    public void Choosing_the_sign_of_its_own_kind_means_unset() => Sta.Run(() =>
    {
        var model = new GameProfile { Kind = ProfileKind.Client, IconGlyph = "castle" };
        var profile = new ProfileViewModel(model, _ => { }, _ => { });

        profile.GlyphChoice = "client";
        Assert.Equal("", profile.IconGlyph);
        Assert.Null(model.IconGlyph);
        Assert.Equal("client", profile.GlyphChoice);

        profile.GlyphChoice = "server"; // знак сервера клиентскому профилю — можно
        Assert.Equal("server", model.IconGlyph);
    });
}

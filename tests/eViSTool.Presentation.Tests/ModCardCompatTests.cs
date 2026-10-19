using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Versioning;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// «Совместимость» в карточке мода: зелёным — только когда модбаза знает релиз под эту ветку игры, красноватым — когда
/// знает, что его нет; остальное — неизвестно, без цвета. Раньше «модбаза не ответила» и «файл не читается» тоже
/// показывались как «VS 1.22.x ✓».
/// </summary>
public sealed class ModCardCompatTests
{
    private static readonly ModVersion Game = ModVersion.ParseOrNull("1.22.7")!;

    private static ModRowViewModel Row(ModStatus status, bool pinned = false)
    {
        var local = new LocalMod("/mods/alpha.zip", new ModInfo { ModId = "alpha", Name = "Alpha", Version = "1.0.0" }, null);
        return new ModRowViewModel(new ModCheckResult { Local = local, Status = status }, [], pinned, ProfileKind.Client);
    }

    [Theory]
    [InlineData(ModStatus.UpToDate, true)]
    [InlineData(ModStatus.UpdateAvailable, true)]
    [InlineData(ModStatus.Pinned, true)]
    [InlineData(ModStatus.NoCompatibleRelease, false)]
    [InlineData(ModStatus.NotChecked, null)]
    [InlineData(ModStatus.NotInModDb, null)]
    [InlineData(ModStatus.CheckFailed, null)]
    [InlineData(ModStatus.Unreadable, null)]
    public void Only_a_modDb_answer_makes_a_mod_compatible_or_not(ModStatus status, bool? expected) =>
        Assert.Equal(expected, ModCardViewModel.Compat(Row(status), Game));

    [Fact]
    public void Without_the_game_version_compatibility_is_unknown() =>
        Assert.Null(ModCardViewModel.Compat(Row(ModStatus.UpToDate), null));
}

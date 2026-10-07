using eViSTool.App.ViewModels;
using eViSTool.Core.Profiles;

namespace eViSTool.App.Tests;

/// <summary>Окно уборки: общая галочка группы — все / ни одного / часть; выбор пунктов сообщает окну.</summary>
public sealed class CleanupViewModelTests
{
    private static CleanupItem Item(string title, bool dflt) =>
        new(CleanupKind.ServerMods, title, [title], 1000, DateTime.UtcNow, dflt);

    [Fact]
    public void GroupCheckbox_FollowsItsItems_AndSetsThemAll()
    {
        var changes = 0;
        var group = new CleanupGroupViewModel(
            new CleanupGroup(CleanupKind.ServerMods, CleanupSafety.Safe, [Item("old", true), Item("fresh", false)]), () => changes++);

        Assert.Null(group.IsChecked);              // часть выбрана
        group.IsChecked = true;
        Assert.All(group.Items, i => Assert.True(i.IsChecked));
        Assert.True(group.IsChecked);
        group.Items[0].IsChecked = false;
        Assert.Null(group.IsChecked);
        group.IsChecked = false;
        Assert.False(group.IsChecked);
        Assert.True(changes >= 3);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(5_000, "4")]                 // КБ
    [InlineData(250L << 20, "250")]          // МБ
    [InlineData(3L << 30, "3")]              // ГБ
    public void Sizes_AreReadable(long bytes, string number) =>
        Assert.StartsWith(number, WorldCopiesViewModel.SizeText(bytes));
}

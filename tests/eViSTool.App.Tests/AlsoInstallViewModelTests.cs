using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.App.Tests;

/// <summary>Окно «Установить также в»: какие строки отмечены заранее, что уходит в ответ, «запомнить».</summary>
public sealed class AlsoInstallViewModelTests
{
    private static ProfileViewModel Profile(string name, ProfileKind kind) =>
        new(new GameProfile { Name = name, Kind = kind }, _ => { });

    private static AlsoChoice Choice(string name, ProfileKind kind, string tag, bool canInstall = true, bool linked = false) =>
        new(Profile(name, kind), tag, $"детали {name}", canInstall, AlreadyThere: false, linked);

    private static AlsoInstallViewModel Make(params AlsoChoice[] others) =>
        new("Мод «Тест» нужен не только здесь.", "Основной", "клиент", "текущий профиль · игра 1.21.3", others);

    [Fact]
    public void FirstRow_IsTheCurrentProfile_CheckedAndLocked()
    {
        var vm = Make(Choice("Сервер", ProfileKind.Server, "s"));

        var current = vm.Rows[0];
        Assert.Equal("Основной", current.Name);
        Assert.Equal("клиент", current.KindText);
        Assert.Equal("текущий профиль · игра 1.21.3", current.Detail);
        Assert.True(current.IsChecked);
        Assert.False(current.IsEnabled);
        Assert.Null(current.Tag);
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public void OtherRows_TakeNameKindAndDetailFromTheChoice()
    {
        var server = Choice("Сервер", ProfileKind.Server, "s");
        var vm = Make(server);

        var row = vm.Rows[1];
        Assert.Equal("Сервер", row.Name);
        Assert.Equal(server.Profile.KindText, row.KindText);
        Assert.Equal("детали Сервер", row.Detail);
        Assert.Equal("s", row.Tag);
    }

    [Fact]
    public void Linked_AreCheckedBeforehand_OnlyIfTheyCanBeInstalled()
    {
        var vm = Make(
            Choice("Связанный", ProfileKind.Server, "a", linked: true),
            Choice("Связанный без версии", ProfileKind.Client, "b", canInstall: false, linked: true),
            Choice("Чужой", ProfileKind.Client, "c"));

        Assert.True(vm.Rows[1].IsChecked);
        Assert.True(vm.Rows[1].IsEnabled);
        Assert.False(vm.Rows[2].IsChecked);
        Assert.False(vm.Rows[2].IsEnabled);
        Assert.False(vm.Rows[3].IsChecked);
        Assert.True(vm.Rows[3].IsEnabled);
    }

    [Fact]
    public void Confirm_ReturnsTagsOfCheckedRows_InOrder_WithoutTheCurrentProfile()
    {
        var vm = Make(
            Choice("Первый", ProfileKind.Server, "a", linked: true),
            Choice("Второй", ProfileKind.Client, "b"),
            Choice("Третий", ProfileKind.Client, "c", linked: true));
        vm.Rows[2].IsChecked = true;

        var answer = vm.Confirm();

        Assert.Equal(["a", "b", "c"], answer.Chosen.Cast<string>());
        Assert.False(answer.Remember);
    }

    [Fact]
    public void Confirm_NothingTicked_MeansOnlyTheCurrentProfile()
    {
        var vm = Make(Choice("Сервер", ProfileKind.Server, "s"));

        Assert.Empty(vm.Confirm().Chosen);
    }

    [Fact]
    public void Untick_RemovesProfileFromTheAnswer()
    {
        var vm = Make(Choice("Сервер", ProfileKind.Server, "s", linked: true));
        vm.Rows[1].IsChecked = false;

        Assert.Empty(vm.Confirm().Chosen);
    }

    [Fact]
    public void Unavailable_NeverGoToTheAnswer_EvenWithATick()
    {
        var vm = Make(Choice("Без версии", ProfileKind.Client, "x", canInstall: false));
        vm.Rows[1].IsChecked = true; // галочку не должно быть видно, но и в ответ она не идёт

        Assert.Empty(vm.Confirm().Chosen);
    }

    [Fact]
    public void Remember_GoesToTheAnswer()
    {
        var vm = Make(Choice("Сервер", ProfileKind.Server, "s", linked: true));
        Assert.False(vm.Remember);
        vm.Remember = true;

        var answer = vm.Confirm();

        Assert.True(answer.Remember);
        Assert.Equal(["s"], answer.Chosen.Cast<string>());
    }

    [Fact]
    public void Texts_AreTheHintAndTheRememberLineWithTheCurrentName()
    {
        var vm = Make();

        Assert.Equal("Мод «Тест» нужен не только здесь.", vm.Hint);
        Assert.Equal(Loc.T("also.remember", "Основной"), vm.RememberText);
        Assert.Contains("Основной", vm.RememberText);
    }

    [Fact]
    public void ChecksAreRaisedToTheView()
    {
        var vm = Make(Choice("Сервер", ProfileKind.Server, "s"));
        var changed = new List<string?>();
        vm.Rows[1].PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Rows[1].IsChecked = true;

        Assert.Contains("IsChecked", changed);
    }
}

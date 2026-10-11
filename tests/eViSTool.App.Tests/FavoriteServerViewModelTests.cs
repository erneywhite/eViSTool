using System.ComponentModel;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.App.Tests;

/// <summary>
/// Окно «Сервер в избранном игры»: что видно при открытии (новый сервер и правка записи), проверка адреса, название по
/// умолчанию, пустой пароль — «без пароля», ответ окна — ветка за веткой, как в окне на WPF. Диска и сети не касается.
/// </summary>
public sealed class FavoriteServerViewModelTests
{
    private static PlayTarget Existing(string name = "Сервер друзей", string address = "play.example.org:42421", string? password = "секрет") =>
        new($"fav:{name}|{address}", PlayTargetKind.Favorite, name, address, password);

    /// <summary>Модель и ответы, с которыми окно закрывалось (список: «сохранили» дважды — это видно).</summary>
    private static (FavoriteServerViewModel Model, List<FavoriteServerChoice> Answers) Open(PlayTarget? existing = null)
    {
        var model = new FavoriteServerViewModel(existing);
        var answers = new List<FavoriteServerChoice>();
        model.Finished += answers.Add;
        return (model, answers);
    }

    // ---- открытие

    [Fact]
    public void New_Opens_WithEmptyFields_AndFocusOnName()
    {
        var (vm, answers) = Open();

        Assert.True(vm.IsNew);
        Assert.Equal(Loc.T("browse.favAdd"), vm.Heading);
        Assert.Equal("", vm.Name);
        Assert.Equal("", vm.Address);
        Assert.Equal("", vm.Password);
        Assert.False(vm.CanSave);
        Assert.False(vm.HasError);
        Assert.Equal("", vm.ErrorText);
        Assert.Null(vm.Result);
        Assert.Empty(answers);
    }

    [Fact]
    public void Edit_Opens_WithRecordFields_AndFocusOnAddress()
    {
        var (vm, _) = Open(Existing());

        Assert.False(vm.IsNew);
        Assert.Equal(Loc.T("browse.favEdit"), vm.Heading);
        Assert.Equal("Сервер друзей", vm.Name);
        Assert.Equal("play.example.org:42421", vm.Address);
        Assert.Equal("секрет", vm.Password);
        Assert.True(vm.CanSave);
        Assert.False(vm.HasError);
    }

    [Fact]
    public void Edit_RecordWithoutPassword_OpensWithEmptyPassword()
    {
        var (vm, _) = Open(Existing(password: null));

        Assert.Equal("", vm.Password);
    }

    // ---- «Сохранить»: ответ

    [Fact]
    public void Save_New_ReturnsNameAddressAndPassword()
    {
        var (vm, answers) = Open();
        vm.Name = "Дом";
        vm.Address = "192.168.1.20";
        vm.Password = "pass";

        vm.SaveCommand.Execute(null);

        var expected = new FavoriteServerChoice("Дом", "192.168.1.20", "pass");
        Assert.Equal(expected, vm.Result);
        Assert.Equal([expected], answers);
        Assert.False(vm.HasError);
    }

    [Fact]
    public void Save_Edit_WithoutChanges_ReturnsTheSameRecord()
    {
        var (vm, answers) = Open(Existing());

        vm.SaveCommand.Execute(null);

        Assert.Equal([new FavoriteServerChoice("Сервер друзей", "play.example.org:42421", "секрет")], answers);
    }

    [Fact]
    public void Save_TrimsNameAndAddress()
    {
        var (vm, answers) = Open();
        vm.Name = "  Дом  ";
        vm.Address = "  play.example.org  ";

        vm.SaveCommand.Execute(null);

        Assert.Equal(new FavoriteServerChoice("Дом", "play.example.org", null), Assert.Single(answers));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Save_EmptyName_BecomesTheAddress(string name)
    {
        var (vm, answers) = Open();
        vm.Name = name;
        vm.Address = "  play.example.org:42421 ";

        vm.SaveCommand.Execute(null);

        var choice = Assert.Single(answers);
        Assert.Equal("play.example.org:42421", choice.Name);
        Assert.Equal("play.example.org:42421", choice.Address);
    }

    [Fact]
    public void Save_Edit_ClearedName_BecomesTheAddress()
    {
        var (vm, answers) = Open(Existing());
        vm.Name = "";

        vm.SaveCommand.Execute(null);

        Assert.Equal("play.example.org:42421", Assert.Single(answers).Name);
    }

    [Fact]
    public void Save_EmptyPassword_MeansNoPassword()
    {
        var (vm, answers) = Open();
        vm.Address = "play.example.org";
        vm.Password = "";

        vm.SaveCommand.Execute(null);

        Assert.Null(Assert.Single(answers).Password);
    }

    [Fact]
    public void Save_Edit_ClearedPassword_RemovesIt()
    {
        var (vm, answers) = Open(Existing());
        vm.Password = "";

        vm.SaveCommand.Execute(null);

        Assert.Null(Assert.Single(answers).Password);
    }

    [Theory]
    [InlineData(" pass ")]  // пароль не обрезается: пробелы по краям могут быть его частью
    [InlineData("   ")]     // и из одних пробелов — тоже пароль, а не «без пароля»
    [InlineData("с пробелом")]
    public void Save_Password_IsKeptAsTyped(string password)
    {
        var (vm, answers) = Open();
        vm.Address = "play.example.org";
        vm.Password = password;

        vm.SaveCommand.Execute(null);

        Assert.Equal(password, Assert.Single(answers).Password);
    }

    [Fact]
    public void Save_NullFromTextBox_IsTreatedAsEmpty()
    {
        // поле ввода может отдать null вместо пустой строки
        var (vm, answers) = Open();
        vm.Name = null!;
        vm.Address = "play.example.org";
        vm.Password = null!;

        vm.SaveCommand.Execute(null);

        Assert.Equal(new FavoriteServerChoice("play.example.org", "play.example.org", null), Assert.Single(answers));
    }

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("play.example.org")]
    [InlineData("play.example.org:42421")]
    [InlineData("[::1]:42420")]
    [InlineData("  host  ")]
    public void Save_GoodAddress_IsAccepted(string address)
    {
        var (vm, answers) = Open();
        vm.Address = address;
        Assert.True(vm.CanSave);

        vm.SaveCommand.Execute(null);

        Assert.Equal(address.Trim(), Assert.Single(answers).Address);
        Assert.False(vm.HasError);
    }

    [Fact]
    public void Save_AddressOfMaxLength_IsAccepted()
    {
        var (vm, answers) = Open();
        vm.Address = new string('a', 255);

        vm.SaveCommand.Execute(null);

        Assert.Single(answers);
    }

    // ---- «Сохранить»: адрес не годится

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("play example.org")]  // пробел внутри
    [InlineData("a,b")]               // запятая разделяет поля записи
    [InlineData("host:1,2")]
    [InlineData("host\tname")]
    [InlineData("host\r\nname")]
    public void Save_BadAddress_ShowsError_AndKeepsWindowOpen(string address)
    {
        var (vm, answers) = Open();
        vm.Name = "Дом";
        vm.Address = address;
        vm.Password = "pass";

        vm.SaveCommand.Execute(null);

        Assert.False(vm.CanSave);
        Assert.True(vm.HasError);
        Assert.Equal(Loc.T("browse.favBadAddress"), vm.ErrorText);
        Assert.Null(vm.Result);
        Assert.Empty(answers);
        // введённое никуда не делось
        Assert.Equal("Дом", vm.Name);
        Assert.Equal(address, vm.Address);
        Assert.Equal("pass", vm.Password);
    }

    [Fact]
    public void Save_AddressLongerThanMax_ShowsError()
    {
        var (vm, answers) = Open();
        vm.Address = new string('a', 256);

        vm.SaveCommand.Execute(null);

        Assert.True(vm.HasError);
        Assert.Empty(answers);
    }

    [Fact]
    public void Save_Edit_BadAddress_ShowsError()
    {
        var (vm, answers) = Open(Existing());
        vm.Address = "play example.org";

        vm.SaveCommand.Execute(null);

        Assert.Equal(Loc.T("browse.favBadAddress"), vm.ErrorText);
        Assert.Empty(answers);
    }

    [Fact]
    public void Error_StaysAfterFixingAddress_UntilNextSave()
    {
        // как в окне на WPF: текст убирает только следующее «Сохранить»
        var (vm, answers) = Open();
        vm.Address = "bad address";
        vm.SaveCommand.Execute(null);
        Assert.True(vm.HasError);

        vm.Address = "good.example.org";

        Assert.True(vm.CanSave);
        Assert.True(vm.HasError);
        Assert.Empty(answers);

        vm.SaveCommand.Execute(null);

        Assert.False(vm.HasError);
        Assert.Equal("", vm.ErrorText);
        Assert.Equal("good.example.org", Assert.Single(answers).Address);
    }

    [Fact]
    public void Error_ReturnsOnSecondBadSave()
    {
        var (vm, _) = Open();
        vm.Address = "a,b";
        vm.SaveCommand.Execute(null);
        vm.Address = "c,d";

        vm.SaveCommand.Execute(null);

        Assert.True(vm.HasError);
        Assert.Equal(Loc.T("browse.favBadAddress"), vm.ErrorText);
    }

    // ---- кнопка и оповещения

    [Fact]
    public void SaveCommand_IsAlwaysAvailable_SoTheErrorCanExplainWhy()
    {
        var (vm, _) = Open();

        Assert.False(vm.CanSave);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void CanSave_FollowsAddress()
    {
        var (vm, _) = Open();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Address = "play.example.org";
        Assert.True(vm.CanSave);
        Assert.Contains(nameof(FavoriteServerViewModel.CanSave), changed);

        vm.Address = "play example.org";
        Assert.False(vm.CanSave);

        vm.Address = "   ";
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void ErrorChange_NotifiesHasError()
    {
        var (vm, _) = Open();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SaveCommand.Execute(null);

        Assert.Contains(nameof(FavoriteServerViewModel.ErrorText), changed);
        Assert.Contains(nameof(FavoriteServerViewModel.HasError), changed);
    }

    [Fact]
    public void Save_WithoutSubscriber_DoesNotFail()
    {
        var vm = new FavoriteServerViewModel(null) { Address = "play.example.org" };

        vm.SaveCommand.Execute(null);

        Assert.Equal("play.example.org", vm.Result?.Address);
    }
}

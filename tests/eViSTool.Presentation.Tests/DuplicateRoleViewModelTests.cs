using System.Reflection;
using eViSTool.Core.Localization;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Окно «Дублировать роль»: подсказка свободного кода, проверка кода по правилам редактора ролей, ошибка под полем и
/// доступность «Создать копию», ответ окна. Роли выдуманные, диск и сеть не трогаются.
/// </summary>
public sealed class DuplicateRoleViewModelTests
{
    private static readonly string[] Standard = ["suvisitor", "suplayer", "sumod", "admin"];

    private static DuplicateRoleViewModel Open(string sourceCode = "suplayer", string sourceName = "Survival Player", params string[] taken) =>
        new(sourceCode, sourceName, taken.Length > 0 ? taken : Standard);

    // ---- открытие

    [Fact]
    public void Opens_WithHeading_DefaultName_AndFreeSuggestedCode()
    {
        var vm = Open();

        Assert.Equal(Loc.T("roles.dupHeading", "Survival Player"), vm.Heading);
        Assert.Equal(Loc.T("roles.dupNameDefault", "Survival Player"), vm.Name);
        Assert.Equal("suplayer2", vm.Code);
        Assert.Equal("", vm.Error);
        Assert.False(vm.HasError);
        Assert.True(vm.CanGo);
        Assert.True(vm.GoCommand.CanExecute(null));
        Assert.Null(vm.Result);
    }

    // ---- подсказка кода

    [Fact]
    public void SuggestCode_AppendsNumberFromTwo()
    {
        Assert.Equal("suplayer2", DuplicateRoleViewModel.SuggestCode("suplayer", ["suplayer", "admin"]));
    }

    [Fact]
    public void SuggestCode_SkipsTakenNumbers()
    {
        Assert.Equal("suplayer4", DuplicateRoleViewModel.SuggestCode("suplayer", ["suplayer", "suplayer2", "suplayer3"]));
    }

    [Fact]
    public void SuggestCode_TakenCodesAreComparedIgnoringCase()
    {
        Assert.Equal("suplayer3", DuplicateRoleViewModel.SuggestCode("suplayer", ["SuPlayer2"]));
    }

    [Fact]
    public void SuggestCode_DoesNotSkipFreeNumberBelowTaken()
    {
        // занято только suplayer3 — берём первый свободный, suplayer2
        Assert.Equal("suplayer2", DuplicateRoleViewModel.SuggestCode("suplayer", ["suplayer3"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("vip role")]
    [InlineData(" vip")]
    [InlineData("vip\t")]
    public void SuggestCode_SourceEmptyOrWithSpaces_UsesRole(string source)
    {
        Assert.Equal("role2", DuplicateRoleViewModel.SuggestCode(source, []));
    }

    [Fact]
    public void SuggestCode_SourceEmptyOrWithSpaces_SkipsTakenRoleNumbers()
    {
        Assert.Equal("role4", DuplicateRoleViewModel.SuggestCode("", ["role2", "role3"]));
    }

    [Fact]
    public void Opens_WithRoleStem_WhenSourceCodeHasSpaces()
    {
        var vm = Open("my role", "Моя роль", "my role");

        Assert.Equal("role2", vm.Code);
        Assert.Equal(Loc.T("roles.dupHeading", "Моя роль"), vm.Heading);
        Assert.Equal(Loc.T("roles.dupNameDefault", "Моя роль"), vm.Name);
        Assert.True(vm.CanGo);
    }

    // ---- проверка кода

    [Fact]
    public void TakenCode_ShowsError_AndBlocksGo()
    {
        var vm = Open();

        vm.Code = "admin";

        Assert.Equal(Loc.T("roles.errCodeTaken", "admin"), vm.Error);
        Assert.True(vm.HasError);
        Assert.False(vm.CanGo);
        Assert.False(vm.GoCommand.CanExecute(null));
    }

    [Fact]
    public void TakenCode_IgnoresCase_AndPadding()
    {
        var vm = Open();

        vm.Code = "  ADMIN ";

        // в тексте ошибки код без пробелов по краям, как его видит проверка
        Assert.Equal(Loc.T("roles.errCodeTaken", "ADMIN"), vm.Error);
        Assert.False(vm.CanGo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyCode_ShowsError_AndBlocksGo(string code)
    {
        var vm = Open();

        vm.Code = code;

        Assert.Equal(Loc.T("roles.errCodeEmpty"), vm.Error);
        Assert.True(vm.HasError);
        Assert.False(vm.CanGo);
        Assert.False(vm.GoCommand.CanExecute(null));
    }

    [Fact]
    public void CodeWithSpaceInside_ShowsError_AndBlocksGo()
    {
        var vm = Open();

        vm.Code = "vip role";

        Assert.Equal(Loc.T("roles.errCodeSpaces"), vm.Error);
        Assert.False(vm.CanGo);
    }

    [Fact]
    public void CodeWithSpacesOnlyAtEdges_IsFine()
    {
        var vm = Open();

        vm.Code = "  vip  ";

        Assert.Equal("", vm.Error);
        Assert.False(vm.HasError);
        Assert.True(vm.CanGo);
    }

    [Fact]
    public void FixingTheCode_ClearsError_AndEnablesGo()
    {
        var vm = Open();
        vm.Code = "admin";
        Assert.False(vm.GoCommand.CanExecute(null));

        vm.Code = "vip";

        Assert.Equal("", vm.Error);
        Assert.False(vm.HasError);
        Assert.True(vm.CanGo);
        Assert.True(vm.GoCommand.CanExecute(null));
    }

    [Fact]
    public void ChangingCode_RaisesErrorHasErrorAndCommandChanges_OnlyWhenTheyChange()
    {
        var vm = Open();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var commandChanged = 0;
        vm.GoCommand.CanExecuteChanged += (_, _) => commandChanged++;

        vm.Code = "admin"; // стало ошибкой
        Assert.Contains(nameof(DuplicateRoleViewModel.Error), changed);
        Assert.Contains(nameof(DuplicateRoleViewModel.HasError), changed);
        Assert.Contains(nameof(DuplicateRoleViewModel.CanGo), changed);
        Assert.Equal(1, commandChanged);

        changed.Clear();
        vm.Code = "sumod"; // та же ошибка, другой текст
        Assert.Contains(nameof(DuplicateRoleViewModel.Error), changed);
        Assert.DoesNotContain(nameof(DuplicateRoleViewModel.CanGo), changed);
        Assert.Equal(1, commandChanged);

        changed.Clear();
        vm.Code = "vip"; // снова годится
        Assert.Contains(nameof(DuplicateRoleViewModel.CanGo), changed);
        Assert.Equal(2, commandChanged);
    }

    [Fact]
    public void NameChange_DoesNotAffectCodeCheck()
    {
        var vm = Open();
        vm.Code = "admin";

        vm.Name = "";

        Assert.Equal(Loc.T("roles.errCodeTaken", "admin"), vm.Error);
        Assert.False(vm.CanGo);
    }

    [Fact]
    public void NullTextFromTheField_IsTreatedAsEmpty()
    {
        var vm = Open();

        vm.Code = null!; // пустое поле Avalonia может отдать null

        Assert.Equal(Loc.T("roles.errCodeEmpty"), vm.Error);
        Assert.False(vm.CanGo);
    }

    // ---- «Создать копию»

    [Fact]
    public void Go_ReturnsTrimmedCodeAndName_AndRaisesFinishedOnce()
    {
        var vm = Open();
        var answers = new List<RoleCopyChoice>();
        vm.Finished += answers.Add;
        vm.Code = "  vip ";
        vm.Name = "  Важный гость  ";

        vm.GoCommand.Execute(null);

        var answer = Assert.Single(answers);
        Assert.Equal(new RoleCopyChoice("vip", "Важный гость"), answer);
        Assert.Equal(answer, vm.Result);
    }

    [Fact]
    public void Go_WithoutEditing_ReturnsSuggestedCodeAndDefaultName()
    {
        var vm = Open();

        vm.GoCommand.Execute(null);

        Assert.Equal(new RoleCopyChoice("suplayer2", Loc.T("roles.dupNameDefault", "Survival Player")), vm.Result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Go_WithEmptyName_ReturnsEmptyName(string name)
    {
        var vm = Open();
        vm.Name = name;

        vm.GoCommand.Execute(null);

        // пустое название — оставить как у исходной роли: решает тот, кто делает копию
        Assert.Equal(new RoleCopyChoice("suplayer2", ""), vm.Result);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("")]
    [InlineData("vip role")]
    public void Go_WithBadCode_DoesNothing(string code)
    {
        var vm = Open();
        var finished = 0;
        vm.Finished += _ => finished++;
        vm.Code = code;

        vm.GoCommand.Execute(null); // Execute команды CanExecute не проверяет — отказывает сама модель

        Assert.Equal(0, finished);
        Assert.Null(vm.Result);
    }

    // ---- снимок окна

    [Fact]
    public void SnapshotCode_ReplacesSuggestedCode_ForTheFirstModelOnly()
    {
        var hook = typeof(DuplicateRoleViewModel).GetField("SnapshotCode", BindingFlags.NonPublic | BindingFlags.Static)!;
        hook.SetValue(null, "admin");
        try
        {
            var first = Open();
            var second = Open();

            Assert.Equal("admin", first.Code);
            Assert.Equal(Loc.T("roles.errCodeTaken", "admin"), first.Error); // занятый код сразу с ошибкой
            Assert.False(first.CanGo);
            Assert.Equal("suplayer2", second.Code); // подсказка берётся один раз
            Assert.True(second.CanGo);
        }
        finally
        {
            hook.SetValue(null, null);
        }
    }
}

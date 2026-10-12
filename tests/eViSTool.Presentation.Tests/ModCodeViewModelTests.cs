using eViSTool.Core.Localization;
using eViSTool.Core.Packs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>Окно «Код набора модов»: «Дальше» — только для разобранного кода, под полем — сколько модов или что не так.</summary>
public sealed class ModCodeViewModelTests
{
    [Fact]
    public void Good_code_is_parsed_and_can_be_accepted()
    {
        Loc.Instance.SetLanguage("ru");
        var code = ModCode.Encode([new ModCodeEntry(10, 1), new ModCodeEntry(20, 0)], Core.Versioning.ModVersion.ParseOrNull("1.22.7"));
        var vm = new ModCodeViewModel { Text = "  " + code + "\n" };
        Assert.True(vm.CanAccept);
        Assert.Equal(2, vm.Set!.Entries.Count);
        Assert.Equal("В коде 2 мода · игра 1.22.7", vm.StatusText);
        Assert.Equal(RowTone.Good, vm.StatusTone);
    }

    [Fact]
    public void Damaged_or_foreign_text_shows_why_and_blocks_next()
    {
        Loc.Instance.SetLanguage("ru");
        var code = ModCode.Encode([new ModCodeEntry(10, 1)]);
        var vm = new ModCodeViewModel { Text = code[..^1] };
        Assert.False(vm.CanAccept);
        Assert.Equal(Loc.T("modcode.damaged"), vm.StatusText);
        Assert.Equal(RowTone.Danger, vm.StatusTone);

        vm.Text = "привет";
        Assert.Equal(Loc.T("modcode.notACode"), vm.StatusText);
        vm.Text = "";
        Assert.Equal("", vm.StatusText);
        Assert.False(vm.CanAccept);
    }
}

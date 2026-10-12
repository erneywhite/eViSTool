using System.ComponentModel;
using eViSTool.Core.Localization;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Окно бана игрока: срок (по умолчанию день), причина без пробелов по краям, ответ в единицах команды /ban.
/// </summary>
public sealed class BanViewModelTests
{
    [Fact]
    public void Opens_with_player_in_heading_day_term_and_empty_reason()
    {
        var vm = new BanViewModel("Вася");

        Assert.Equal(Loc.T("players.banHeading", "Вася"), vm.Heading);
        Assert.Equal(BanTerm.Day, vm.Term);
        Assert.Equal("", vm.Reason);
        Assert.Null(vm.Result);
    }

    [Fact]
    public void Heading_keeps_player_name_as_is()
    {
        // фигурные скобки в нике не должны ломать подстановку
        Assert.Equal(Loc.T("players.banHeading", "{0}x{1}"), new BanViewModel("{0}x{1}").Heading);
    }

    [Theory]
    [InlineData(BanTerm.Hour, 1, "hour")]
    [InlineData(BanTerm.Day, 1, "day")]
    [InlineData(BanTerm.Week, 1, "week")]
    [InlineData(BanTerm.Month, 30, "day")]   // «месяц» — тридцать дней
    [InlineData(BanTerm.Forever, 100, "year")] // «навсегда» — сто лет: так сервер хранит бессрочный бан
    public void Ok_answers_with_term_in_ban_command_units(BanTerm term, int amount, string unit)
    {
        var vm = new BanViewModel("Вася") { Term = term };

        vm.OkCommand.Execute(null);

        Assert.Equal(new BanChoice(amount, unit, ""), vm.Result);
    }

    [Fact]
    public void Ok_without_touching_anything_bans_for_a_day_without_reason()
    {
        var vm = new BanViewModel("Вася");

        vm.OkCommand.Execute(null);

        Assert.Equal(new BanChoice(1, "day", ""), vm.Result);
    }

    [Theory]
    [InlineData("грифер", "грифер")]
    [InlineData("  грифер \r\n", "грифер")] // пробелы и перевод строки по краям не нужны
    [InlineData("сломал  мост", "сломал  мост")] // внутри текст не трогаем
    [InlineData("   ", "")]
    [InlineData("", "")]
    public void Reason_is_trimmed(string typed, string expected)
    {
        var vm = new BanViewModel("Вася") { Reason = typed };

        vm.OkCommand.Execute(null);

        Assert.Equal(expected, vm.Result?.Reason);
        Assert.Equal(typed, vm.Reason); // сама строка в поле остаётся как ввели
    }

    [Fact]
    public void Missing_reason_text_counts_as_empty()
    {
        // поле ввода при очистке может отдать null
        var vm = new BanViewModel("Вася") { Reason = null! };

        vm.OkCommand.Execute(null);

        Assert.Equal("", vm.Result?.Reason);
    }

    [Fact]
    public void Ok_raises_Finished_once_with_the_result()
    {
        var vm = new BanViewModel("Вася") { Term = BanTerm.Week, Reason = " спам " };
        var finished = new List<BanChoice>();
        vm.Finished += finished.Add;

        vm.OkCommand.Execute(null);

        var choice = Assert.Single(finished);
        Assert.Equal(new BanChoice(1, "week", "спам"), choice);
        Assert.Same(vm.Result, choice);
    }

    [Fact]
    public void Nothing_is_answered_until_Ok()
    {
        var vm = new BanViewModel("Вася");
        var finished = false;
        vm.Finished += _ => finished = true;

        vm.Term = BanTerm.Forever;
        vm.Reason = "читы";

        Assert.False(finished);
        Assert.Null(vm.Result);
    }

    [Fact]
    public void Ok_is_always_available()
    {
        Assert.True(new BanViewModel("Вася").OkCommand.CanExecute(null));
    }

    [Fact]
    public void Term_and_reason_changes_are_announced_to_the_window()
    {
        var vm = new BanViewModel("Вася");
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Term = BanTerm.Month;
        vm.Reason = "x";

        Assert.Equal([nameof(BanViewModel.Term), nameof(BanViewModel.Reason)], changed);
    }

    [Fact]
    public void ChoiceFor_matches_what_Ok_answers()
    {
        var vm = new BanViewModel("Вася") { Term = BanTerm.Month, Reason = "  x " };

        Assert.Equal(BanViewModel.ChoiceFor(BanTerm.Month, "  x "), vm.Choice);
        Assert.Equal(new BanChoice(30, "day", "x"), vm.Choice);
        Assert.Equal(new BanChoice(1, "day", ""), BanViewModel.ChoiceFor(BanTerm.Day, null));
    }
}

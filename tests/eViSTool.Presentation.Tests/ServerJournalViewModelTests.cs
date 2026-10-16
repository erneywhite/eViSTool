using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>Строки вкладки «Журнал»: кто и откуда, подпись действия, время по часам окна.</summary>
public sealed class ServerJournalViewModelTests
{
    private static readonly DateTime At = new(2026, 10, 16, 18, 4, 0, DateTimeKind.Utc);

    [Fact]
    public void Remote_ShowsComputerAndAddress()
    {
        var row = ServerJournalViewModel.Row(new JournalEntry(At, "modInstall", ["carryon_1.1.0.zip"], "ERNEY-PC", "192.168.31.5", true));

        Assert.Equal("ERNEY-PC · 192.168.31.5", row.WhoText);
        Assert.Equal(Loc.T("journal.act.modInstall", "carryon_1.1.0.zip"), row.ActionText);
        Assert.False(row.Warn);
        // время — по часам этого компьютера
        Assert.Equal(Dates.DayMonthClock(At.ToLocalTime()), row.TimeText);
    }

    [Fact]
    public void Local_IsMarkedAsTheServerComputer()
    {
        var row = ServerJournalViewModel.Row(new JournalEntry(At, "stop", [], "SERVER", null, false));
        Assert.Equal(Loc.T("journal.local", "SERVER"), row.WhoText);
    }

    [Fact]
    public void WrongKey_IsAWarning_AndUnknownNameIsQuestionMark()
    {
        var row = ServerJournalViewModel.Row(new JournalEntry(At, "authFailed", [], null, "85.12.44.7", true));
        Assert.True(row.Warn);
        Assert.Equal("? · 85.12.44.7", row.WhoText);
    }

    [Fact]
    public void UnknownAction_FromANewerAgent_IsShownAsIs()
    {
        var row = ServerJournalViewModel.Row(new JournalEntry(At, "somethingNew", ["x", "y"], "PC", null, false));
        Assert.Equal("somethingNew x y", row.ActionText);
    }
}

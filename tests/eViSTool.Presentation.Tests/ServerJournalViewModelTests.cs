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
    public void Details_AreInTheWindowLanguage_SecretsStayHidden()
    {
        var row = ServerJournalViewModel.Row(new JournalEntry(At, "players", [], "PC", "10.0.0.2", true,
        [
            new JournalDetail("playerWhitelist", ["Anna", "on"]),
            new JournalDetail("whitelistMode", ["Off", "On"]),
            new JournalDetail("secret", ["Password"]),
            new JournalDetail("modVersion", ["CarryOn", "1.10.4", "1.10.3"]),
            new JournalDetail("brandNew", ["x"]), // агент новее окна
        ]));

        Assert.Equal(
        [
            Loc.T("journal.d.whitelistOn", "Anna"),
            Loc.T("journal.d.whitelistMode", Loc.T("journal.mode.off"), Loc.T("journal.mode.on")),
            Loc.T("journal.d.secret", "Password"),
            Loc.T("journal.d.modUpdated", "CarryOn", "1.10.4", "1.10.3"),
            "brandNew x",
        ], row.Details);
        Assert.True(row.HasDetails);
        Assert.False(row.CanExpand);
    }

    [Fact]
    public void ManyDetails_FirstFive_ThenExpandAndFoldBack()
    {
        var details = Enumerable.Range(1, 12).Select(i => new JournalDetail("field", [$"k{i}", "1", "2"])).ToList();
        var row = ServerJournalViewModel.Row(new JournalEntry(At, "config", [], "PC", null, false, details));
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(row.CanExpand);
        Assert.Equal(JournalRow.Folded, row.ShownDetails.Count);
        Assert.Equal(Loc.T("journal.detailsMore", 7), row.ExpandText);

        row.ToggleExpandedCommand.Execute(null);
        Assert.Equal(12, row.ShownDetails.Count);
        Assert.Equal(Loc.T("journal.detailsLess"), row.ExpandText);
        Assert.Contains(nameof(JournalRow.ShownDetails), changed);
        Assert.Contains(nameof(JournalRow.ExpandText), changed);

        row.ToggleExpandedCommand.Execute(null);
        Assert.Equal(JournalRow.Folded, row.ShownDetails.Count);
    }

    [Fact]
    public void OldEntries_WithoutDetails_HaveNone()
    {
        var row = ServerJournalViewModel.Row(new JournalEntry(At, "players", [], "PC", null, false));
        Assert.False(row.HasDetails);
        Assert.Empty(row.Details);
    }

    [Fact]
    public void UnknownAction_FromANewerAgent_IsShownAsIs()
    {
        var row = ServerJournalViewModel.Row(new JournalEntry(At, "somethingNew", ["x", "y"], "PC", null, false));
        Assert.Equal("somethingNew x y", row.ActionText);
    }
}

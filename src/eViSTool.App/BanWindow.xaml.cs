using System.Windows;
using eViSTool.Core.Localization;

namespace eViSTool.App;

/// <summary>Бан игрока: срок и причина. Срок — в единицах команды /ban (minute, hour, day, week, year).</summary>
public partial class BanWindow : Window
{
    public int Amount { get; private set; } = 1;
    public string Unit { get; private set; } = "day";
    public string Reason { get; private set; } = "";

    public BanWindow(string player)
    {
        InitializeComponent();
        Heading.Text = Loc.T("players.banHeading", player);
        Loaded += (_, _) => ReasonBox.Focus();
    }

    private void Go_Click(object sender, RoutedEventArgs e)
    {
        // «навсегда» — сто лет: так сервер и хранит бессрочный бан
        (Amount, Unit) = Hour.IsChecked == true ? (1, "hour")
            : Week.IsChecked == true ? (1, "week")
            : Month.IsChecked == true ? (30, "day")
            : Forever.IsChecked == true ? (100, "year")
            : (1, "day");
        Reason = ReasonBox.Text.Trim();
        DialogResult = true;
    }
}

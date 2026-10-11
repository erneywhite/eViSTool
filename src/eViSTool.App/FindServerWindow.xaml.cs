using System.Windows;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.App;

/// <summary>Окно «Найти сервер»: общий список серверов и избранное игры. DataContext — FindServerViewModel.</summary>
public partial class FindServerWindow : Window
{
    private readonly FindServerViewModel _vm;

    public FindServerWindow(FindServerViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await _vm.Load();
        };
    }

    private void AllTab_Checked(object sender, RoutedEventArgs e) => _vm.IsFavoritesTab = false;

    private void SortPlayers_Checked(object sender, RoutedEventArgs e) => _vm.ByName = false;

    private static string? AskPassword(string server) => AppWindows.Current.AskServerPassword(server);

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ServerRowViewModel row) return;
        if (await _vm.PlayAsync(row, AskPassword)) Close();
    }

    private static (string Name, string Address, string? Password)? EditServer(Core.Game.PlayTarget? old) =>
        AppWindows.Current.EditFavoriteServer(old) is { } c ? (c.Name, c.Address, c.Password) : null;

    private void AddManual_Click(object sender, RoutedEventArgs e) => _vm.SaveFavorite(null, EditServer);

    private void EditFavorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ServerRowViewModel { Favorite: { } f }) _vm.SaveFavorite(f, EditServer);
    }

    private void AddFavorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ServerRowViewModel row) _vm.AddFavorite(row, AskPassword);
    }
}

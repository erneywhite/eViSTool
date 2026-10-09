using System.Windows;
using eViSTool.App.ViewModels;

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

    private string? AskPassword(string server)
    {
        var dlg = new ServerPasswordWindow(server) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.Password : null;
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ServerRowViewModel row) return;
        if (await _vm.PlayAsync(row, AskPassword)) Close();
    }

    private void AddFavorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ServerRowViewModel row) _vm.AddFavorite(row, AskPassword);
    }
}

using System.Windows;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;

namespace eViSTool.App;

/// <summary>Сервер в избранном игры: название, адрес (с портом или без), пароль — как в меню самой игры.</summary>
public partial class FavoriteServerWindow : Window
{
    public string ServerName { get; private set; } = "";
    public string Address { get; private set; } = "";
    public string? Password { get; private set; }

    /// <param name="existing">null — новый сервер.</param>
    public FavoriteServerWindow(PlayTarget? existing)
    {
        InitializeComponent();
        Heading.Text = Loc.T(existing is null ? "browse.favAdd" : "browse.favEdit");
        NameBox.Text = existing?.Name ?? "";
        AddressBox.Text = existing?.Address ?? "";
        // пароль из избранного игры — он там открытым текстом, в поле — точками
        PasswordBox.Password = existing?.Password ?? "";
        Loaded += (_, _) => (existing is null ? NameBox : AddressBox).Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var address = AddressBox.Text.Trim();
        if (!GameFavorites.IsValidAddress(address))
        {
            ErrorText.Text = Loc.T("browse.favBadAddress");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Address = address;
        ServerName = NameBox.Text.Trim() is { Length: > 0 } name ? name : address;
        Password = PasswordBox.Password.Length > 0 ? PasswordBox.Password : null;
        DialogResult = true;
    }
}

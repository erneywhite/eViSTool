using System.Windows;
using eViSTool.Core.Localization;

namespace eViSTool.App;

/// <summary>Пароль сервера — перед входом или добавлением в избранное.</summary>
public partial class ServerPasswordWindow : Window
{
    public string Password { get; private set; } = "";

    public ServerPasswordWindow(string server)
    {
        InitializeComponent();
        Heading.Text = Loc.T("browse.passwordFor", server);
        Loaded += (_, _) => Box.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Password = Box.Password;
        DialogResult = true;
    }
}

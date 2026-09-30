using System.Windows;
using System.Windows.Controls;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;

namespace eViSTool.App;

/// <summary>
/// Название нового профиля. Для серверного — сразу видно, какая папка данных будет создана:
/// она получает имя профиля (…\VintagestoryData\ServerProfiles\имя).
/// </summary>
public partial class NewProfileWindow : Window
{
    private readonly ProfileKind _kind;

    public string ProfileName { get; private set; } = "";

    /// <summary>Папка данных нового серверного профиля (для клиентского — null).</summary>
    public string? DataDir { get; private set; }

    public NewProfileWindow(ProfileKind kind, string suggestedName)
    {
        InitializeComponent();
        _kind = kind;
        Title = Heading.Text = Loc.T(kind == ProfileKind.Server ? "newprofile.titleServer" : "newprofile.titleClient");
        FolderPanel.Visibility = kind == ProfileKind.Server ? Visibility.Visible : Visibility.Collapsed;
        NameBox.Text = suggestedName;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        var name = NameBox.Text.Trim();
        OkButton.IsEnabled = name.Length > 0;
        if (_kind == ProfileKind.Server)
            FolderText.Text = name.Length > 0 ? ServerProfileLayout.SuggestDir(GameInstall.DefaultDataDir, name) : "";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) return;
        ProfileName = name;
        if (_kind == ProfileKind.Server) DataDir = ServerProfileLayout.SuggestDir(GameInstall.DefaultDataDir, name);
        DialogResult = true;
    }
}

using System.IO;
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

    /// <summary>Что делать с уже существующими данными сервера.</summary>
    public enum ExistingMode { None, Import, UseInPlace, Remote }

    /// <summary>Для Remote: код подключения к серверу на другом компьютере.</summary>
    public Core.Server.Remote.ConnectionCode? RemoteCode { get; private set; }

    public ExistingMode Existing { get; private set; }

    /// <summary>Папка существующих данных сервера (для Import и UseInPlace).</summary>
    public string? ExistingDir { get; private set; }

    public NewProfileWindow(ProfileKind kind, string suggestedName)
    {
        InitializeComponent();
        _kind = kind;
        Title = Heading.Text = Loc.T(kind == ProfileKind.Server ? "newprofile.titleServer" : "newprofile.titleClient");
        FolderPanel.Visibility = kind == ProfileKind.Server ? Visibility.Visible : Visibility.Collapsed;
        ExistingPanel.Visibility = FolderPanel.Visibility;
        RemotePanel.Visibility = FolderPanel.Visibility;
        // сервер уже работал со стандартной папкой (так бывает на машине, где стоит только сервер) — подставляем её
        if (kind == ProfileKind.Server)
            ExistingBox.Text = ProfileResolver.LooksLikeServerData(GameInstall.DefaultDataDir) ? GameInstall.DefaultDataDir : "";
        NameBox.Text = suggestedName;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        var name = NameBox.Text.Trim();
        OkButton.IsEnabled = name.Length > 0;
        ExistingBox_TextChanged(sender, e);
        if (_kind == ProfileKind.Server)
            FolderText.Text = name.Length > 0 ? ServerProfileLayout.SuggestDir(GameInstall.DefaultDataDir, name) : "";
    }

    private void ExistingBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        var dir = ExistingBox.Text.Trim();
        var configs = dir.Length > 0 && Directory.Exists(dir) ? ProfileCloner.FindConfigs(dir) : [];
        var ok = configs.Count > 0;
        ImportButton.IsEnabled = UseButton.IsEnabled = ok && NameBox.Text.Trim().Length > 0;
        ExistingInfo.Text = dir.Length == 0 ? ""
            : ok ? Loc.T("newprofile.existingFound", string.Join(", ", configs.Select(c => c.WorldName is { Length: > 0 } w ? $"{c.FileName} ({w})" : c.FileName)))
            : Loc.T("newprofile.existingNone");
        ExistingInfo.Foreground = (System.Windows.Media.Brush)FindResource(ok || dir.Length == 0 ? "Forest.Muted" : "Forest.Danger");
    }

    private void BrowseExisting_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("newprofile.existingPick"), InitialDirectory = ExistingBox.Text.Trim() };
        if (dlg.ShowDialog(this) == true) ExistingBox.Text = dlg.FolderName;
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { NameBox.Focus(); return; }
        var dlg = new ConnectCodeWindow(Loc.T("newprofile.connectOk")) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Code is not { } code) return;
        ProfileName = name;
        Existing = ExistingMode.Remote;
        RemoteCode = code;
        DataDir = "";
        DialogResult = true;
    }

    private void Import_Click(object sender, RoutedEventArgs e) => FinishExisting(ExistingMode.Import);
    private void UseInPlace_Click(object sender, RoutedEventArgs e) => FinishExisting(ExistingMode.UseInPlace);

    private void FinishExisting(ExistingMode mode)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) return;
        ProfileName = name;
        Existing = mode;
        ExistingDir = ExistingBox.Text.Trim();
        DataDir = ExistingDir;
        DialogResult = true;
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

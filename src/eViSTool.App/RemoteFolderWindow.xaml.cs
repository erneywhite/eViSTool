using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.App;

/// <summary>
/// Выбор папки на машине с сервером (другой компьютер, в том числе Linux): стандартный диалог Windows показывает только
/// эту машину, поэтому папки берутся у агента. Только папки, без файлов.
/// </summary>
public partial class RemoteFolderWindow : Window
{
    private readonly IServerData _data;
    private DirListing? _current;
    private bool _filling;

    /// <summary>Выбранная папка (полный путь на машине с сервером).</summary>
    public string? Selected { get; private set; }

    public RemoteFolderWindow(IServerData data, string? start, string heading)
    {
        InitializeComponent();
        _data = data;
        Heading.Text = heading;
        Loaded += async (_, _) => await GoAsync(start);
    }

    private async Task GoAsync(string? path)
    {
        Error.Visibility = Visibility.Collapsed;
        try
        {
            var listing = await _data.ListDirsAsync(path);
            _current = listing;
            _filling = true;
            PathBox.Text = listing.Path;
            Dirs.ItemsSource = listing.Dirs;
            Up.IsEnabled = listing.Parent is not null;
            Roots.ItemsSource = listing.Roots;
            Roots.Visibility = listing.Roots.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            Roots.SelectedItem = listing.Roots.FirstOrDefault(r => listing.Path.StartsWith(r, StringComparison.OrdinalIgnoreCase));
            _filling = false;
            if (listing.Denied) Show(Loc.T("rfolder.denied"));
            else if (path is { Length: > 0 } asked && !SamePath(asked, listing.Path)) Show(Loc.T("rfolder.notFound", asked));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or IOException)
        {
            _filling = false;
            Show(_data.IsRemote ? RemoteSecret.Describe(ex) : ex.Message);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

    private void Show(string text)
    {
        Error.Text = text;
        Error.Visibility = Visibility.Visible;
    }

    // путь на машине с сервером собираем её разделителем: на Linux — «/», на Windows — «\»
    private string Child(string name) =>
        _current is not { } c ? name
            : c.Path.EndsWith('/') || c.Path.EndsWith('\\') ? c.Path + name
            : c.Path + (c.Path.Contains('/') && !c.Path.Contains('\\') ? "/" : "\\") + name;

    private async void Dirs_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Dirs.SelectedItem is string name) await GoAsync(Child(name));
    }

    private async void Dirs_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Dirs.SelectedItem is not string name) return;
        e.Handled = true;
        await GoAsync(Child(name));
    }

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await GoAsync(PathBox.Text);
    }

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.Parent is { } parent) await GoAsync(parent);
    }

    private async void Roots_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && Roots.SelectedItem is string root) await GoAsync(root);
    }

    /// <summary>Выбрать: отмеченную в списке подпапку, иначе — ту, где стоим.</summary>
    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        Selected = Dirs.SelectedItem is string name ? Child(name) : _current.Path;
        DialogResult = true;
    }
}

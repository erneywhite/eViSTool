using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using eViSTool.Core.Localization;

namespace eViSTool.App;

/// <summary>Просмотр скриншотов мода внутри программы: стрелки/клавиши ← → листают, Esc закрывает.</summary>
public partial class ScreenshotWindow : Window
{
    private readonly IReadOnlyList<string> _urls;
    private int _index;

    public ScreenshotWindow(IReadOnlyList<string> urls, int index, string title)
    {
        InitializeComponent();
        _urls = urls;
        _index = Math.Clamp(index, 0, urls.Count - 1);
        Title = title;

        // почти во весь рабочий стол — скриншоты должны быть крупными
        var area = SystemParameters.WorkArea;
        Width = area.Width * 0.85;
        Height = area.Height * 0.85;
        Show(_index);
    }

    private void Show(int index)
    {
        _index = (index + _urls.Count) % _urls.Count;
        Counter.Text = $"{_index + 1} / {_urls.Count}";
        PrevButton.Visibility = NextButton.Visibility = _urls.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        Loading.Text = Loc.T("common.loading");
        Loading.Visibility = Visibility.Visible;
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(_urls[_index]);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        if (image.IsDownloading)
        {
            image.DownloadCompleted += (_, _) => Loading.Visibility = Visibility.Collapsed;
            image.DownloadFailed += (_, _) => Loading.Text = Loc.T("shots.loadFailed");
        }
        else Loading.Visibility = Visibility.Collapsed;
        Picture.Source = image;
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Show(_index - 1);
    private void Next_Click(object sender, RoutedEventArgs e) => Show(_index + 1);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Browser_Click(object sender, RoutedEventArgs e) => Shell.OpenUrl(_urls[_index]);

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: Show(_index - 1); break;
            case Key.Right: Show(_index + 1); break;
            case Key.Escape: Close(); break;
            default: return;
        }
        e.Handled = true;
    }
}

using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>Просмотр скриншотов мода: модально, ответа нет (как ShowDialog в окне на WPF).</summary>
    public void ShowScreenshots(IReadOnlyList<string> urls, int index, string title) =>
        new ScreenshotWindow { DataContext = new ScreenshotViewerViewModel(urls, index, title) }.ShowModal<bool>();
}

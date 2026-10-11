using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

/// <summary>
/// Снимок окна для сверки с окном на WPF (EVISTOOL_SNAPSHOT, см. <see cref="Snapshots"/>): та же навигация и тот же
/// размер области окна, что у окна на WPF, → PNG → выход.
/// </summary>
internal static class SnapshotRunner
{
    public static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, SnapshotRequest request)
    {
        try
        {
            if (desktop.MainWindow is not { DataContext: MainViewModel main } window) return;
            window.WindowState = WindowState.Normal;
            window.Width = request.Width;   // у Avalonia Width/Height окна — это область без рамки, как содержимое у WPF
            window.Height = request.Height;
            Snapshots.Navigate(main, request);
            await Task.Delay(request.Wait);
            using var bitmap = new RenderTargetBitmap(new PixelSize(request.Width, request.Height));
            bitmap.Render(window);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutPath))!);
            bitmap.Save(request.OutPath, new PngBitmapEncoderOptions());
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
        desktop.Shutdown();
    }
}

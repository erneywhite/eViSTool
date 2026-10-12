using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

/// <summary>
/// Снимок окна (EVISTOOL_SNAPSHOT, см. <see cref="Snapshots"/>): переход на экран, заданный
/// размер области окна → PNG → выход.
/// </summary>
internal static class SnapshotRunner
{
    public static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, SnapshotRequest request)
    {
        try
        {
            if (desktop.MainWindow is not { DataContext: MainViewModel main } window) return;
            window.WindowState = WindowState.Normal;
            window.Width = request.Width;   // у Avalonia Width/Height окна — это область без рамки
            window.Height = request.Height;
            // окно программы (dialog.*) модальное: Navigate не вернётся, пока его не закроют, — поэтому переход отдельно,
            // а снимок — по таймеру (вложенный цикл диспетчера его выполнит)
            Dispatcher.UIThread.Post(() => Snapshots.Navigate(main, request));
            await Task.Delay(request.Wait);
            var target = Snapshots.IsDialog(request) ? desktop.Windows.LastOrDefault(w => w.IsVisible && w != window) ?? window : window;
            var size = target == window ? new PixelSize(request.Width, request.Height)
                                        : new PixelSize((int)Math.Ceiling(target.Bounds.Width), (int)Math.Ceiling(target.Bounds.Height));
            using var bitmap = new RenderTargetBitmap(size);
            bitmap.Render(target);
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

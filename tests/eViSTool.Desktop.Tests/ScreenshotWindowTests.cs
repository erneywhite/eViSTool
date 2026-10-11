using System.Net;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Desktop.Controls;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>Окно просмотра скриншотов: клавиши, подписи «Загрузка…» и «Не удалось», стрелки только при нескольких картинках.</summary>
public sealed class ScreenshotWindowTests : IDisposable
{
    private static readonly string[] Urls = ["https://img.test/1.png", "https://img.test/2.png", "https://img.test/3.png"];

    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "evistool-tests", "shots-" + Guid.NewGuid().ToString("N"));
    private readonly Gate _gate = new();

    public ScreenshotWindowTests()
    {
        RemoteImage.HandlerForTests = _gate;
        RemoteImage.CacheDir = _cacheDir;
        RemoteImage.ClearMemory();
    }

    public void Dispose()
    {
        _gate.Release();
        RemoteImage.HandlerForTests = null;
        RemoteImage.ClearMemory();
        try { Directory.Delete(_cacheDir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private static (ScreenshotWindow Window, ScreenshotViewerViewModel Model) Open(params string[] urls)
    {
        var model = new ScreenshotViewerViewModel(urls, 0, "Мод — скриншоты");
        var window = new ScreenshotWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, model);
    }

    private static Control Named(Window window, string automationId) =>
        window.GetVisualDescendants().OfType<Control>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    private static void Press(Window window, Key key, PhysicalKey physical) => window.KeyPress(key, RawInputModifiers.None, physical, null);

    [AvaloniaFact]
    public void Arrow_keys_page_around_and_escape_closes()
    {
        var (window, model) = Open(Urls);
        Assert.Equal("Мод — скриншоты", window.Title);

        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Assert.Equal(1, model.Index);
        Press(window, Key.Left, PhysicalKey.ArrowLeft);
        Press(window, Key.Left, PhysicalKey.ArrowLeft);
        Assert.Equal(2, model.Index); // с первой — на последнюю

        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void Picture_follows_the_model()
    {
        var (window, model) = Open(Urls);
        var picture = (Image)Named(window, "ScreenshotPicture");
        Assert.Equal(Urls[0], RemoteImage.GetSource(picture));
        Assert.Equal("1 / 3", ((TextBlock)Named(window, "ScreenshotCounter")).Text);

        model.NextCommand.Execute(null);
        Assert.Equal(Urls[1], RemoteImage.GetSource(picture));
        Assert.Equal("2 / 3", ((TextBlock)Named(window, "ScreenshotCounter")).Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Shows_loading_text_then_failure_text()
    {
        var (window, _) = Open(Urls[0]);
        var loading = Named(window, "ScreenshotLoading");
        var failed = Named(window, "ScreenshotFailed");
        Assert.True(loading.IsVisible);
        Assert.False(failed.IsVisible);

        _gate.Release(); // сеть ответила «нет такой картинки»
        await Wait.Until(() => failed.IsVisible);
        Assert.False(loading.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Arrows_only_when_there_is_something_to_page()
    {
        var (several, _) = Open(Urls);
        Assert.True(Named(several, "ScreenshotPrev").IsVisible);
        Assert.True(Named(several, "ScreenshotNext").IsVisible);
        several.Close();

        var (one, _) = Open(Urls[0]);
        Assert.False(Named(one, "ScreenshotPrev").IsVisible);
        Assert.False(Named(one, "ScreenshotNext").IsVisible);
        one.Close();
    }

    /// <summary>Сеть, которая молчит, пока её не отпустят, а потом отвечает 404.</summary>
    private sealed class Gate : HttpMessageHandler
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _open.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await _open.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}

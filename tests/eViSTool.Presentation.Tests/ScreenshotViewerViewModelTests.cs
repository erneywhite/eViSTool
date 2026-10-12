using eViSTool.Core.Localization;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>Просмотр скриншотов: листание по кругу, счётчик, «Открыть в браузере».</summary>
public sealed class ScreenshotViewerViewModelTests
{
    private static readonly string[] Urls = ["https://x.test/1.png", "https://x.test/2.png", "https://x.test/3.png"];

    private static ScreenshotViewerViewModel Open(int index = 0, params string[] urls) =>
        new(urls.Length > 0 ? urls : Urls, index, "Мод — скриншоты");

    [Fact]
    public void StartsOnGivenPicture_WithCounter()
    {
        var vm = Open(1);
        Assert.Equal(1, vm.Index);
        Assert.Equal("https://x.test/2.png", vm.CurrentUrl);
        Assert.Equal("2 / 3", vm.Counter);
        Assert.Equal(3, vm.Count);
        Assert.True(vm.HasSeveral);
        Assert.Equal("Мод — скриншоты", vm.Title);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(100, 2)]
    public void StartIndex_IsClampedToList(int start, int expected) => Assert.Equal(expected, Open(start).Index);

    [Fact]
    public void Next_WrapsFromLastToFirst()
    {
        var vm = Open(2);
        vm.NextCommand.Execute(null);
        Assert.Equal(0, vm.Index);
        Assert.Equal("1 / 3", vm.Counter);
        Assert.Equal("https://x.test/1.png", vm.CurrentUrl);
    }

    [Fact]
    public void Previous_WrapsFromFirstToLast()
    {
        var vm = Open(0);
        vm.PreviousCommand.Execute(null);
        Assert.Equal(2, vm.Index);
        Assert.Equal("3 / 3", vm.Counter);
        vm.PreviousCommand.Execute(null);
        Assert.Equal(1, vm.Index);
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(-4, 2)]
    [InlineData(3, 0)]
    [InlineData(7, 1)]
    public void Show_WrapsAnyNumber(int number, int expected)
    {
        var vm = Open(0);
        vm.Show(number);
        Assert.Equal(expected, vm.Index);
    }

    [Fact]
    public void Paging_RaisesChangeForUrlAndCounter()
    {
        var vm = Open(0);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.NextCommand.Execute(null);
        Assert.Contains(nameof(ScreenshotViewerViewModel.CurrentUrl), changed);
        Assert.Contains(nameof(ScreenshotViewerViewModel.Counter), changed);
        Assert.Contains(nameof(ScreenshotViewerViewModel.Index), changed);
    }

    [Fact]
    public void SinglePicture_NoPagingAndNoChange()
    {
        var vm = Open(0, "https://x.test/only.png");
        var changed = 0;
        vm.PropertyChanged += (_, _) => changed++;
        Assert.False(vm.HasSeveral);
        Assert.Equal("1 / 1", vm.Counter);
        vm.NextCommand.Execute(null);
        vm.PreviousCommand.Execute(null);
        Assert.Equal(0, vm.Index);
        Assert.Equal(0, changed); // картинка не перезагружается зря
    }

    [Fact]
    public void EmptyList_DoesNotThrow()
    {
        var vm = new ScreenshotViewerViewModel([], 3, "x");
        Assert.Null(vm.CurrentUrl);
        Assert.Equal("", vm.Counter);
        Assert.False(vm.HasSeveral);
        vm.NextCommand.Execute(null);
        vm.PreviousCommand.Execute(null);
        vm.OpenInBrowserCommand.Execute(null);
        Assert.Equal(0, vm.Index);
    }

    [Fact]
    public void EmptyTitle_FallsBackToCommonOne()
    {
        Assert.Equal(Loc.T("shots.windowTitle"), new ScreenshotViewerViewModel(Urls, 0, " ").Title);
    }

    [Fact]
    public void OpenInBrowser_OpensCurrentPicture()
    {
        var previous = Ui.IsSet ? Ui.Platform : new TestUiPlatform();
        var recorder = new UrlRecorder();
        Ui.Platform = recorder;
        try
        {
            var vm = Open(0);
            vm.OpenInBrowserCommand.Execute(null);
            vm.NextCommand.Execute(null);
            vm.OpenInBrowserCommand.Execute(null);
            Assert.Equal(["https://x.test/1.png", "https://x.test/2.png"], recorder.Opened);
        }
        finally
        {
            Ui.Platform = previous;
        }
    }

    /// <summary>Платформа, которая только запоминает, какие ссылки просили открыть; остального модель не касается.</summary>
    private sealed class UrlRecorder : IUiPlatform
    {
        public List<string> Opened { get; } = [];

        public void OpenUrl(string url) => Opened.Add(url);

        public Answer Ask(string text, AskButtons buttons, Answer? byDefault = null, bool warning = false) => throw new NotSupportedException();
        public void Notify(string text, NoticeKind kind) => throw new NotSupportedException();
        public bool IsUiThread => true;
        public void Post(Action action) => throw new NotSupportedException();
        public void PostIdle(Action action) => throw new NotSupportedException();
        public void Invoke(Action action) => throw new NotSupportedException();
        public IUiTimer CreateTimer(TimeSpan interval) => throw new NotSupportedException();
        public IReadOnlyList<string>? PickFiles(string title, IReadOnlyList<FileFilter> filters, bool multiple) => throw new NotSupportedException();
        public string? PickSaveFile(string title, IReadOnlyList<FileFilter> filters, string suggestedName, string? folder = null, bool confirmOverwrite = true) => throw new NotSupportedException();
        public string? PickFolder(string title, string? initialFolder) => throw new NotSupportedException();
        public void SetClipboard(string text) => throw new NotSupportedException();
        public void OpenFolder(string dir) => throw new NotSupportedException();
        public void ShowInFolder(string path) => throw new NotSupportedException();
        public void MoveToTrash(string path) => throw new NotSupportedException();
        public void OpenTrash() => throw new NotSupportedException();
        public bool IsMainWindowVisible => false;
        public void RestartAfterClose(string exe) => throw new NotSupportedException();
        public string WriteCrashLog(Exception ex) => throw new NotSupportedException();
    }
}

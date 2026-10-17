using System.Net;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Desktop.Controls;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

public sealed partial class XamlUsageView : UserControl
{
    public XamlUsageView() => AvaloniaXamlLoader.Load(this);
}

public sealed record Choice(string Name);

public sealed class UsageModel : ObservableObject
{
    private RowTone _tone = RowTone.Good;
    private ConfigLoadState _state = ConfigLoadState.Loading;
    private string? _colorText;

    public string Initials => "AB";
    public string? Logo { get; init; }
    public IReadOnlyList<string> Items { get; } = ["one", "two"];
    public IReadOnlyList<Choice> Colors { get; } = [new("Красный"), new("Лесной")];
    public IEnumerable<string?> IconSearchDirs { get; init; } = [];
    public RecordingCommand Open { get; } = new();
    public ICommand OpenCommand => Open;

    public RowTone Tone { get => _tone; set => SetProperty(ref _tone, value); }
    public ConfigLoadState State { get => _state; set => SetProperty(ref _state, value); }
    public string? ColorText { get => _colorText; set => SetProperty(ref _colorText, value); }
}

/// <summary>Строки разметки из XamlUsageView: собрались компилятором XAML и работают.</summary>
public sealed class XamlUsageTests : IDisposable
{
    private readonly string _cacheDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "evistool-tests", "xaml-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        RemoteImage.HandlerForTests = null;
        RemoteImage.ClearMemory();
        try { Directory.Delete(_cacheDir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private sealed class LogoServer(TaskCompletionSource release) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await release.Task;
            var bitmap = new WriteableBitmap(new PixelSize(200, 200), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) };
        }
    }

    [AvaloniaFact]
    public async Task Markup_lines_work()
    {
        var release = new TaskCompletionSource();
        RemoteImage.HandlerForTests = new LogoServer(release);
        RemoteImage.CacheDir = _cacheDir;
        RemoteImage.ClearMemory();

        var model = new UsageModel { Logo = "https://img.test/xaml-logo.png" };
        var view = new XamlUsageView { DataContext = model };
        var window = new Window { Width = 400, Height = 400, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        T Find<T>(string name) where T : Control => view.FindControl<T>(name)!;

        // логотип: пока грузится — буквы и «Загрузка…»; загрузился — картинка 92 px, букв нет
        Assert.True(Find<TextBlock>("Initials").IsVisible);
        Assert.True(Find<TextBlock>("Loading").IsVisible);
        release.SetResult();
        await Wait.Until(() => Find<Image>("Logo").Source is not null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(Find<TextBlock>("Initials").IsVisible);
        Assert.False(Find<TextBlock>("Loading").IsVisible);
        Assert.False(Find<TextBlock>("Failed").IsVisible);
        Assert.Equal(92, ((Bitmap)Find<Image>("Logo").Source!).PixelSize.Width);

        // класс по тону
        Assert.Contains("good", Find<Ellipse>("Dot").Classes);
        model.Tone = RowTone.Danger;
        Assert.Contains("danger", Find<Ellipse>("Dot").Classes);
        Assert.DoesNotContain("good", Find<Ellipse>("Dot").Classes);

        // чипы и видимость по перечислению
        Assert.True(Find<RadioButton>("LoadingChip").IsChecked);
        Assert.False(Find<TextBlock>("LoadedPanel").IsVisible);
        Find<RadioButton>("LoadedChip").IsChecked = true;
        Assert.Equal(ConfigLoadState.Loaded, model.State);
        Assert.True(Find<TextBlock>("LoadedPanel").IsVisible);

        // фокус и выделение при открытии
        var nameBox = Find<TextBox>("NameBox");
        Assert.True(nameBox.IsFocused);
        Assert.Equal(nameBox.Text!.Length, nameBox.SelectionEnd - nameBox.SelectionStart);

        // двойной щелчок по строке
        var row = Find<ListBox>("List").ContainerFromIndex(1)!;
        Mouse.Click(window, row, x: 10, count: 2);
        Assert.Equal(["two"], model.Open.Calls);

        // поле со списком: выбор пишет Name, у второго — DisplayMemberBinding
        Find<ComboBox>("ColorBox").SelectedIndex = 1;
        Assert.Equal("Лесной", model.ColorText);
        var chat = Find<ComboBox>("ChatBox");
        chat.SelectedIndex = 0;
        Assert.Equal("Красный", chat.Text);

        // игры нет — буквы «VS»
        Assert.Null(Find<Image>("GameIconImage").Source);
        Assert.True(Find<TextBlock>("GameLetters").IsVisible);

        // миниатюры по ширине ряда
        var shot = (Border)((ContentPresenter)Find<ItemsControl>("Shots").ContainerFromIndex(0)!).Child!;
        Assert.Equal(Math.Round(shot.Width * 9 / 16), shot.Height);
        Assert.True(shot.Width > 0);
        window.Close();
    }
}

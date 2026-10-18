using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Core.Localization;
using eViSTool.Desktop.Controls;

namespace eViSTool.Desktop.Tests;

public sealed partial class RunInlinesView : UserControl
{
    public RunInlinesView() => AvaloniaXamlLoader.Load(this);
}

/// <summary>Строка таблицы, как строка мода: modid и пометка «не нужен на этой стороне».</summary>
public sealed class RunRowModel(string name, string modId, string note) : ObservableObject
{
    private string _modId = modId, _note = note;

    public string Name { get; } = name;
    public string ModId { get => _modId; set => SetProperty(ref _modId, value); }

    public string SideNote
    {
        get => _note;
        set { if (SetProperty(ref _note, value)) OnPropertyChanged(nameof(IsUnneeded)); }
    }

    public bool IsUnneeded => _note.Length > 10;
}

/// <summary>Страница: счётчик с подписью, версия и строки таблицы.</summary>
public sealed class RunPageModel : ObservableObject
{
    private int _modCount = 3;
    private string _installedLabel = "installed";
    private string _version = "0.12.1";

    public int ModCount { get => _modCount; set => SetProperty(ref _modCount, value); }
    public string InstalledLabel { get => _installedLabel; set => SetProperty(ref _installedLabel, value); }
    public string Version { get => _version; set => SetProperty(ref _version, value); }
    public ObservableCollection<RunRowModel> Rows { get; } = [];
}

/// <summary>
/// Подписи из нескольких Run при смене языка и данных на лету — та ошибка, из-за которой в «Моих модах» на Linux
/// пропадали счётчики и строка modid. Причина была в Avalonia 12 до 12.1.4: кусок поменялся, пока страница скрыта, кадр
/// нарисовал подпись без раскладки и запомнил пустую строку в кэше строк; после показа подпись пустая. До 12.1.4 её
/// обходил свой RunTextBlock, теперь подписи — обычные TextBlock, а эти тесты следят, чтобы ошибка не вернулась. Смена языка лишь самый частый повод: язык меняют на «Настройках», остальные страницы в это время
/// скрыты. Перевод {local:Tr} тут ни при чём — он меняет Run.Text так же, как {Binding}.
/// </summary>
public sealed class RunInlinesTests : IDisposable
{
    private readonly string _language = Loc.Instance.Language;

    public void Dispose() => Loc.Instance.SetLanguage(_language);

    private sealed record Page(Window Window, RunInlinesView View, Control Other, RunPageModel Model);

    /// <summary>Окно с двумя страницами, как в программе: видна одна, вторая скрыта через IsVisible.</summary>
    private static Page Open()
    {
        Loc.Instance.SetLanguage("en");
        var model = new RunPageModel();
        for (var i = 0; i < 5; i++) model.Rows.Add(new RunRowModel("Mod " + i, "mod" + i, " · server"));
        var view = new RunInlinesView { DataContext = model };
        var other = new TextBlock { Text = "другая страница", IsVisible = false };
        var window = new Window { Width = 600, Height = 400, Content = new Panel { Children = { view, other } } };
        window.Show();
        Frame();
        return new Page(window, view, other, model);
    }

    /// <summary>Раскладка и кадр — как на экране, где кадры рисуются и пока страница скрыта.</summary>
    private static void Frame()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void ShowPage(Page page, bool visible)
    {
        page.View.IsVisible = visible;
        page.Other.IsVisible = !visible;
        Frame();
    }

    /// <summary>
    /// Язык — на русский, подписи и число — длиннее. Строки либо пересобраны (новые объекты, как «Мои моды» после смены
    /// языка), либо поменяны на месте.
    /// </summary>
    private static void Change(RunPageModel model, bool replaceRows)
    {
        Loc.Instance.SetLanguage("ru");
        model.InstalledLabel = "установлено модов";
        model.ModCount = 12;
        model.Version = "0.12.1.1";
        if (replaceRows)
        {
            model.Rows.Clear();
            for (var i = 0; i < 5; i++) model.Rows.Add(new RunRowModel("Mod " + i, "longer_mod_" + i, " · только сервер"));
        }
        else
            foreach (var row in model.Rows) (row.ModId, row.SideNote) = (row.ModId + "_x", " · только сервер");
    }

    private static TextBlock Named(Page page, string name) => page.View.FindControl<TextBlock>(name)!;

    private static List<TextBlock> Cells(Page page, string cls) =>
        page.View.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains(cls)).ToList();

    /// <summary>
    /// Подписи-обходы: счётчик, версия с переводом (она же в заданном размере и упёршаяся в MaxWidth) и строки таблицы.
    /// </summary>
    private static List<TextBlock> Fixed(Page page) =>
        [Named(page, "Counter"), Named(page, "Translated"), Named(page, "Sized"), Named(page, "Clamped"), .. Cells(page, "row")];

    private static string Expected(TextBlock t) => string.Concat(t.Inlines!.OfType<Run>().Select(r => r.Text));

    /// <summary>Ширина разложенного текста (у подписи с заданным размером DesiredSize — это размер, а не текст).</summary>
    private static double Width(TextBlock t) => t.TextLayout.WidthIncludingTrailingWhitespace;

    /// <summary>Сколько точек подписи на кадре отличается от фона — самого частого цвета её прямоугольника.</summary>
    private static int Ink(WriteableBitmap frame, Window window, Control control)
    {
        var origin = control.TranslatePoint(default, window)!.Value;
        var rect = new PixelRect((int)origin.X, (int)origin.Y, (int)control.Bounds.Width, (int)control.Bounds.Height)
            .Intersect(new PixelRect(frame.PixelSize));
        if (rect.Width == 0 || rect.Height == 0) return 0;
        var pixels = new int[rect.Width * rect.Height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { frame.CopyPixels(rect, handle.AddrOfPinnedObject(), pixels.Length * 4, rect.Width * 4); }
        finally { handle.Free(); }
        var background = pixels.GroupBy(p => p).MaxBy(g => g.Count())!.Key;
        return pixels.Count(p => p != background);
    }

    private static WriteableBitmap Capture(Window window)
    {
        Frame();
        return window.CaptureRenderedFrame() ?? throw new InvalidOperationException("кадр не отрисовался");
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void PiecesChangedWhileThePageIsHidden_AreDrawnWhenItIsShown(bool replaceRows)
    {
        var page = Open();
        var before = Fixed(page).Select(Width).ToList();
        Assert.All(before, w => Assert.True(w > 0));

        ShowPage(page, visible: false);
        Change(page.Model, replaceRows);
        Frame(); // кадр, пока страница скрыта: он и портил подписи
        ShowPage(page, visible: true);

        // перевод в куске сменился, как и привязки
        Assert.Equal(Loc.T("about.version"), ((Run)Named(page, "Translated").Inlines![0]).Text);
        var rows = Cells(page, "row");
        Assert.Equal(5, rows.Count);
        Assert.All(rows, r => Assert.Equal(Loc.T("mods.sortName"), ((Run)r.Inlines![^1]).Text));

        var frame = Capture(page.Window);
        var shown = Fixed(page);
        Assert.Equal(before.Count, shown.Count);
        for (var i = 0; i < shown.Count; i++)
        {
            var t = shown[i];
            // новый текст длиннее прежнего: подпись не пустая и не осталась прежней
            Assert.True(Width(t) > before[i], $"«{Expected(t)}»: ширина {Width(t)}");
            Assert.True(Ink(frame, page.Window, t) > 20, $"«{Expected(t)}» не нарисована");
        }
        page.Window.Close();
    }

    [AvaloniaFact]
    public void PiecesChangedOnAVisiblePage_AreDrawn()
    {
        var page = Open();
        Change(page.Model, replaceRows: true);
        var frame = Capture(page.Window);

        foreach (var t in Fixed(page).Append(Named(page, "PlainCounter")).Concat(Cells(page, "plain")))
        {
            Assert.True(t.DesiredSize.Width > 0, $"«{Expected(t)}»: ширина 0");
            Assert.True(Ink(frame, page.Window, t) > 20, $"«{Expected(t)}» не нарисована");
        }
        page.Window.Close();
    }

    [AvaloniaFact]
    public void MeasuredInTheSamePlaceAfterAFrameWithoutLayout_GetsItsRealWidth()
    {
        // место измерения совпало с местом раскладки — раскладка, собранная кадром без кусков, сама не сбросится
        var run = new Run("коротко");
        var text = new TextBlock { Inlines = [run, new Run(" и ясно")] };
        var place = new Size(300, 20);
        text.Measure(place);
        text.Arrange(new Rect(place));
        var before = text.DesiredSize.Width;

        run.Text = "гораздо длиннее, чем было"; // куски сброшены, измерения ещё не было
        _ = text.TextLayout;                    // так кадр рисует подпись на скрытой странице
        text.Measure(place);

        Assert.True(text.DesiredSize.Width > before, $"ширина {text.DesiredSize.Width}, была {before}");
    }
}

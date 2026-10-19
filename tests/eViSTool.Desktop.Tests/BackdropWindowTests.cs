using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Threading;
using eViSTool.Desktop.Controls;
using GlyphPath = Avalonia.Controls.Shapes.Path;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// «Окно» в подложку боковой панели (Controls/BackdropWindow, docs/design.md, раздел 5): в нём ровно то, что лежит под
/// этим местом окна. С фоном из игры — те же пиксели той же картинки, что видны без страницы поверх, при любом размере
/// окна и положении карточки. Без картинки — те же «горизонтали», что нарисовала бы боковая панель шириной во всё окно,
/// с тем же сдвигом плавания, и плавание не перерисовывает ничего правее карточки. Стенд повторяет главное окно: картинка
/// на всё окно, слева панель 210 px с линиями, справа непрозрачная страница, на ней карточка.
/// </summary>
public sealed class BackdropWindowTests
{
    private sealed record Stand(Window Window, Grid Root, Image Picture, Border Side, GlyphPath Lines, ContourDrift Drift,
        Border Page, ScrollViewer Scroll, Border Card, BackdropWindow Hole);

    private static Stand Build()
    {
        var picture = new Image { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapInterpolationMode(picture, BitmapInterpolationMode.HighQuality);
        var lines = new GlyphPath
        {
            Data = ContourLines.Geometry, Stroke = Brushes.White, StrokeThickness = 1, Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        var side = new Border { Width = 210, HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true, Child = lines };
        // без вуали: в «окне» картинка как есть — её пиксели сравниваются с картинкой под страницей
        var hole = new BackdropWindow { Background = Brushes.Black };
        // карточка по высоте захватывает группу линий 205…276 px от верха окна
        var card = new Border { Margin = new Thickness(40, 190, 30, 0), Height = 140, VerticalAlignment = VerticalAlignment.Top, Child = hole };
        // страница прокручивается, как «О программе»; полосы прокрутки не видно — она не мешает проверке пикселей
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            Content = new StackPanel { Children = { card, new Border { Height = 600 } } },
        };
        var page = new Border { Margin = new Thickness(230, 0, 0, 0), Background = Brushes.Black, Child = scroll };
        var root = new Grid { Background = Brushes.Black, Children = { picture, side, page } };
        BackdropWindow.SetPicture(root, picture);
        BackdropWindow.SetLines(root, lines);
        var window = new Window { Width = 600, Height = 400, Content = root };
        return new Stand(window, root, picture, side, lines, new ContourDrift(window, lines), page, scroll, card, hole);
    }

    private static void Show(Window window)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        // окно без экрана само из фокуса не уходит, а линиям плыть незачем: сдвиг тесты ставят сами (см. ContourDriftTests)
        typeof(WindowBase).GetMethod("HandleDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Картинка с мелким узором: сдвиг хоть на пиксель меняет почти все пиксели «окна».</summary>
    private static WriteableBitmap Pattern(int width, int height)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var frame = bitmap.Lock();
        var row = new byte[width * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                row[x * 4] = (byte)(x * 37 + y * 11);
                row[x * 4 + 1] = (byte)((x ^ y) * 23);
                row[x * 4 + 2] = (byte)(y * 41 + x * 5);
                row[x * 4 + 3] = 255;
            }
            Marshal.Copy(row, 0, frame.Address + y * frame.RowBytes, row.Length);
        }
        return bitmap;
    }

    private static Rect InWindow(Visual visual, Window window)
    {
        var at = BackdropWindow.Origin(visual, window) ?? throw new InvalidOperationException("не в окне");
        return new Rect(at, visual.Bounds.Size);
    }

    private static byte[] Pixels(WriteableBitmap frame, PixelRect area)
    {
        var bytes = new byte[area.Width * area.Height * 4];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { frame.CopyPixels(area, handle.AddrOfPinnedObject(), bytes.Length, area.Width * 4); }
        finally { handle.Free(); }
        return bytes;
    }

    private static WriteableBitmap Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        return window.CaptureRenderedFrame() ?? throw new InvalidOperationException("кадр не отрисовался");
    }

    /// <summary>Наибольшая разница каналов между кадрами в области.</summary>
    private static int Difference(byte[] a, byte[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Max();

    /// <summary>
    /// Кадр со страницей и кадр без неё: в «окне» — те же пиксели, что лежат под ним. Для чувствительности — то же место
    /// без страницы, но на 2 px в сторону, должно отличаться.
    /// </summary>
    private static void AssertSeesWhatIsUnder(Stand s, string what, int tolerance = 2)
    {
        s.Page.IsVisible = true;
        Dispatcher.UIThread.RunJobs();
        var hole = InWindow(s.Hole, s.Window);
        var area = new PixelRect((int)hole.X, (int)hole.Y, (int)hole.Width, (int)hole.Height);
        var through = Pixels(Frame(s.Window), area);

        s.Page.IsVisible = false;
        var under = Frame(s.Window);
        s.Page.IsVisible = true;
        Dispatcher.UIThread.RunJobs();

        var difference = Difference(through, Pixels(under, area));
        Assert.True(difference <= tolerance, $"{what}: в «окне» не то, что под ним (разница каналов до {difference})");
        Assert.True(Difference(through, Pixels(under, area.Translate(new PixelVector(2, 0)))) > 20, $"{what}: проверка не видит сдвига");
    }

    [AvaloniaFact]
    public void Picture_shows_the_same_pixels_as_the_backdrop_under_it_at_any_size_and_place()
    {
        var s = Build();
        // пропорции не как у окна: картинка растянута с обрезкой по бокам (UniformToFill)
        s.Picture.Source = Pattern(120, 60);
        Show(s.Window);

        var shown = Assert.IsType<BackdropWindow.Shown>(s.Hole.ShownPicture);
        Assert.Same(s.Picture.Source, shown.Source); // тот же Bitmap, а не копия
        Assert.False(s.Hole.LinesShown);
        AssertSeesWhatIsUnder(s, "окно 600 × 400");

        // окно уже и выше: другой масштаб и обрезка сверху-снизу — пересчёт при раскладке
        s.Window.Width = 520;
        s.Window.Height = 460;
        Dispatcher.UIThread.RunJobs();
        AssertSeesWhatIsUnder(s, "окно 520 × 460");

        // страницу прокрутили — картинка в карточке тоже на новом месте
        s.Scroll.Offset = new Vector(0, 57);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(190 - 57, InWindow(s.Hole, s.Window).Y, 3);
        AssertSeesWhatIsUnder(s, "прокрутка");

        // карточка переехала по раскладке — тоже
        s.Card.Margin = new Thickness(60, 150, 20, 0);
        Dispatcher.UIThread.RunJobs();
        AssertSeesWhatIsUnder(s, "карточка правее");

        // смена картинки — без всякой раскладки окна
        s.Picture.Source = Pattern(90, 90);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(s.Picture.Source, s.Hole.ShownPicture!.Source);
        AssertSeesWhatIsUnder(s, "другая картинка");
        s.Window.Close();
    }

    [AvaloniaFact]
    public void Without_a_picture_lines_continue_the_side_panel_and_drift_with_it()
    {
        var s = Build();
        Show(s.Window);
        Assert.Null(s.Hole.ShownPicture);
        Assert.True(s.Hole.LinesShown);
        // сдвиг плавания — тот же объект, что у линий панели: его X двигает ContourDrift
        Assert.Same(s.Lines.RenderTransform, s.Hole.LinesCopy.RenderTransform);
        Vector origin = InWindow(s.Lines, s.Window).Position - InWindow(s.Hole, s.Window).Position;
        Assert.Equal(origin, s.Hole.LinesOrigin);

        // панель «до самого окна»: без страницы её линии лежат там же, где копия в карточке, при любом сдвиге
        s.Side.Width = 600;
        foreach (var phase in new[] { 0, 100.5, 170 }) // панель рисует узкий узор (до 748 px): дальше сравнивать не с чем
        {
            s.Drift.Seek(phase);
            AssertSeesWhatIsUnder(s, $"сдвиг {phase}", tolerance: 6); // те же кривые, но другой длины путь: сглаживание краёв чуть иное
        }

        // появилась картинка — линий нет; галочку сняли (картинки снова нет) — линии
        s.Picture.Source = Pattern(60, 40);
        Dispatcher.UIThread.RunJobs();
        Assert.False(s.Hole.LinesShown);
        Assert.NotNull(s.Hole.ShownPicture);
        s.Picture.Source = null;
        Dispatcher.UIThread.RunJobs();
        Assert.True(s.Hole.LinesShown);
        Assert.Null(s.Hole.ShownPicture);
        s.Window.Close();
    }

    /// <summary>
    /// Плавание не перерисовывает ничего правее карточки: Avalonia отмечает перерисованное (отладочный слой DirtyRects
    /// закрашивает его полупрозрачным случайным цветом), правее карточки страница остаётся чёрной. Только карточкой и
    /// панелью перерисовка не ограничена: Avalonia по умолчанию сводит перерисованное за кадр в один прямоугольник
    /// (SingleDirtyRectTracker), и когда видны и панель, и карточка, это прямоугольник от панели до правого края карточки
    /// на всю высоту окна — страница над карточкой и под ней тоже перерисовывается (цена — docs/design.md, раздел 5,
    /// «Окно в подложку»). Проверяем, что прямоугольник не шире.
    /// </summary>
    [AvaloniaFact]
    public void Drift_redraws_nothing_right_of_the_card()
    {
        var s = Build();
        Show(s.Window);
        s.Window.RendererDiagnostics.DebugOverlays = RendererDebugOverlays.DirtyRects;
        Frame(s.Window); // первый кадр с отладочным слоем перерисовывает всё

        // y = 200 — над линиями карточки: без отметки там чёрный фон. Цвет отметки случайный и может выйти почти чёрным —
        // в карточке хватит отметки хотя бы в одном из двух кадров
        var marked = 0;
        for (var i = 1; i <= 2; i++)
        {
            s.Drift.Seek(i * 0.2);
            var frame = Frame(s.Window);
            if (Pixel(frame, 400, 200) != Colors.Black) marked++;
            foreach (var (x, y) in new[] { (580, 50), (590, 200), (595, 390) }) // карточка кончается на 570
                Assert.Equal((x, y, Colors.Black), (x, y, Pixel(frame, x, y)));
        }
        Assert.NotEqual(0, marked);
        s.Window.RendererDiagnostics.DebugOverlays = RendererDebugOverlays.None;
        s.Window.Close();
    }

    private static Color Pixel(WriteableBitmap frame, int x, int y)
    {
        var bytes = Pixels(frame, new PixelRect(x, y, 1, 1));
        return frame.Format == PixelFormat.Rgba8888
            ? Color.FromArgb(bytes[3], bytes[0], bytes[1], bytes[2])
            : Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }

    [Fact]
    public void Wide_pattern_covers_the_window_at_any_drift()
    {
        var narrow = ContourLines.Geometry.Bounds;
        foreach (var width in new[] { 300.0, 1800, 4200 })
        {
            var strip = ContourLines.Strip(width).Bounds;
            Assert.True(strip.Right >= width + ContourLines.Period, $"узор до {strip.Right}, нужно {width + ContourLines.Period}");
            Assert.Equal(narrow.Left, strip.Left, 3);
            Assert.Equal((narrow.Top, narrow.Bottom), (strip.Top, strip.Bottom));
        }
        // строится только на рост: на ту же и меньшую ширину — та же геометрия
        Assert.Same(ContourLines.Strip(4200), ContourLines.Strip(1000));
    }
}

using System.Diagnostics;
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
/// «Горизонтали» заглушки под боковой панелью плывут (docs/design.md, раздел 5): по таймеру, на 1 px за шаг, а не на
/// каждом кадре; только пока окно на экране, в фокусе и не свёрнуто, линии видны и движение не выключено — иначе таймер
/// стоит. Через период узора сдвиг возвращается к нулю, а узор при этом тот же: скачка нет. Сдвиг перерисовывает только
/// боковую панель. Стенд повторяет главное окно: слева панель 210 px с ClipToBounds, в ней линии.
/// </summary>
public sealed class ContourDriftTests
{
    private const int Side = 210;

    private static (Window Window, GlyphPath Lines, ContourDrift Drift) Stand()
    {
        var lines = new GlyphPath
        {
            Data = ContourLines.Geometry, Stroke = Brushes.White, StrokeThickness = 1, Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        var side = new Border { Width = Side, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left, Child = lines };
        var window = new Window { Width = 600, Height = 400, Content = new Panel { Background = Brushes.Black, Children = { side } } };
        return (window, lines, new ContourDrift(window, lines));
    }

    private static void Show(Window window)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
    }

    // окно без экрана само из фокуса не уходит (другое окно его не отнимает), а сообщение системы об уходе окно
    // разбирает в закрытом HandleDeactivated — его и зовём
    private static void Deactivate(Window window)
    {
        var handle = typeof(WindowBase).GetMethod("HandleDeactivated", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handle);
        handle.Invoke(window, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsActive);
    }

    private static TranslateTransform Shift(GlyphPath lines) => Assert.IsType<TranslateTransform>(lines.RenderTransform);

    [AvaloniaFact]
    public async Task Lines_shift_by_timer_one_pixel_a_step_without_layout()
    {
        var (window, lines, drift) = Stand();
        Assert.False(drift.IsMoving); // окно ещё не показано
        Show(window);
        Assert.True(window.IsActive);
        Assert.True(drift.IsMoving);

        var steps = new List<double>();
        Shift(lines).Changed += (_, _) => steps.Add(drift.Offset);
        var layouts = 0;
        window.LayoutUpdated += (_, _) => layouts++;
        var watch = Stopwatch.StartNew();
        // кадры тикают часто (~раз в 10 мс), а сдвиг — только по таймеру
        await Wait.Frames(1100);
        var most = (int)(watch.ElapsedMilliseconds / ContourDrift.Interval.TotalMilliseconds) + 1;

        Assert.InRange(steps.Count, 3, most);
        for (var i = 0; i < steps.Count; i++) Assert.Equal(-(i + 1), steps[i]); // по 1 px влево
        Assert.Equal(0, layouts); // двигается только RenderTransform: раскладка не пересчитывается
        window.Close();
        Assert.False(drift.IsMoving);
    }

    [AvaloniaFact]
    public async Task Timer_stops_when_inactive_minimized_lines_hidden_or_motion_off()
    {
        var (window, lines, drift) = Stand();
        Show(window);
        Assert.True(drift.IsMoving);

        Deactivate(window);
        Assert.False(drift.IsMoving);
        var offset = drift.Offset;
        await Wait.Idle(500); // стоит по-настоящему: таймер не тикает
        Assert.Equal(offset, drift.Offset);
        window.Activate();
        Dispatcher.UIThread.RunJobs();
        Assert.True(drift.IsMoving);

        window.WindowState = WindowState.Minimized;
        Assert.False(drift.IsMoving);
        window.WindowState = WindowState.Normal;
        Assert.True(drift.IsMoving);

        lines.IsVisible = false; // под панелью фон из игры
        Assert.False(drift.IsMoving);
        lines.IsVisible = true;
        Assert.True(drift.IsMoving);

        Motion.SetEnabled(window, false); // в системе выключены анимации: наследуется от окна
        Assert.False(drift.IsMoving);
        Motion.SetEnabled(window, true);
        Assert.True(drift.IsMoving);

        window.Close();
        Assert.False(drift.IsMoving);
    }

    [AvaloniaFact]
    public void After_a_period_offset_returns_to_zero_one_pixel_further()
    {
        var (_, _, drift) = Stand();
        for (var i = 1; i < ContourLines.Period; i++)
        {
            drift.Advance();
            Assert.Equal(-i, drift.Offset);
        }
        // последний шаг периода: −Period — это тот же узор, что и 0 (см. Pattern_repeats_after_a_period)
        drift.Advance();
        Assert.Equal(0, drift.Offset);
        drift.Advance();
        Assert.Equal(-1, drift.Offset);
    }

    [AvaloniaFact]
    public void Pattern_repeats_after_a_period()
    {
        // узора хватает на панель при любом сдвиге
        var bounds = ContourLines.Geometry.Bounds;
        Assert.True(bounds.Left <= 0, $"узор начинается с {bounds.Left}");
        Assert.True(bounds.Right >= ContourLines.Period + Side, $"узор кончается на {bounds.Right}");

        var (window, lines, _) = Stand();
        Show(window);
        Deactivate(window); // таймер не мешает: сдвиг задаём сами
        byte[] At(double x)
        {
            Shift(lines).X = x;
            Dispatcher.UIThread.RunJobs();
            return PanelPixels(window.CaptureRenderedFrame() ?? throw new InvalidOperationException("кадр не отрисовался"));
        }

        var start = At(0);
        Assert.NotEqual(start, At(-1)); // сдвиг правда виден на кадре
        Assert.Equal(start, At(-ContourLines.Period));
        Assert.Equal(At(-37), At(-37 - ContourLines.Period));
        window.Close();
    }

    /// <summary>
    /// Перерисовка от сдвига — только в границах боковой панели: Avalonia отмечает перерисованное (отладочный слой
    /// DirtyRects закрашивает его полупрозрачным случайным цветом), справа от панели фон кадра остаётся чистым.
    /// </summary>
    [AvaloniaFact]
    public void Shift_redraws_only_the_side_panel()
    {
        var (window, _, drift) = Stand();
        Show(window);
        Deactivate(window);
        window.RendererDiagnostics.DebugOverlays = RendererDebugOverlays.DirtyRects;
        Assert.NotNull(window.CaptureRenderedFrame()); // первый кадр с отладочным слоем перерисовывает всё

        // y = 200 — между группами линий: без отметки там чёрный фон. Цвет отметки случайный и может выйти почти
        // чёрным — внутри панели хватит того, что отмечено хотя бы в одном из двух кадров
        var marked = 0;
        for (var i = 0; i < 2; i++)
        {
            drift.Advance();
            Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("кадр не отрисовался");
            if (Pixel(frame, 100, 200) != Colors.Black && Pixel(frame, Side - 5, 200) != Colors.Black) marked++;
            foreach (var x in new[] { Side + 5, 400, 595 })
                Assert.Equal(Colors.Black, Pixel(frame, x, 200));
        }
        Assert.NotEqual(0, marked);
        window.RendererDiagnostics.DebugOverlays = RendererDebugOverlays.None;
        window.Close();
    }

    private static byte[] PanelPixels(WriteableBitmap frame)
    {
        var height = frame.PixelSize.Height;
        var bytes = new byte[Side * height * 4];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect(0, 0, Side, height), handle.AddrOfPinnedObject(), bytes.Length, Side * 4); }
        finally { handle.Free(); }
        return bytes;
    }

    private static Color Pixel(WriteableBitmap frame, int x, int y)
    {
        var bytes = new byte[4];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4); }
        finally { handle.Free(); }
        return frame.Format == PixelFormat.Rgba8888
            ? Color.FromArgb(bytes[3], bytes[0], bytes[1], bytes[2])
            : Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }
}

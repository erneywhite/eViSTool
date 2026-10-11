using Avalonia;
using Avalonia.Interactivity;
using eViSTool.Desktop.Platform;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// Просмотр скриншотов мода: крупная картинка, стрелки ←/→ и кнопки по краям листают по кругу, Esc закрывает.
/// Что показано и куда листать — в <see cref="ScreenshotViewerViewModel"/>; здесь только размер окна.
/// </summary>
public partial class ScreenshotWindow : DialogWindow
{
    /// <summary>Доля рабочей области экрана: скриншоты должны быть крупными.</summary>
    internal const double ScreenShare = 0.85;

    /// <summary>Размер окна целиком, с рамкой, в независимых единицах; null — экран не известен.</summary>
    private Size? _outer;

    /// <summary>Модель — в <c>DataContext</c>: <c>new ScreenshotWindow { DataContext = new ScreenshotViewerViewModel(…) }</c>.</summary>
    public ScreenshotWindow()
    {
        InitializeComponent();
        FitToScreen();
    }

    /// <summary>
    /// Почти во весь рабочий стол — как в окне на WPF (SystemParameters.WorkArea). Экран берём у владельца, как будущее
    /// положение окна (CenterOwner); окну до показа экран не известен. Рабочая область в пикселях, размер окна в
    /// независимых единицах — делим на масштаб экрана. Экрана нет (редкий случай) — остаются размеры из разметки.
    /// </summary>
    private void FitToScreen()
    {
        var owner = AvaloniaUiPlatform.TopWindow() ?? AvaloniaUiPlatform.MainWindow;
        if (owner?.Screens is not { } screens) return;
        if ((screens.ScreenFromWindow(owner) ?? screens.Primary) is not { } screen) return;
        var area = screen.WorkingArea;
        _outer = new Size(Math.Floor(area.Width / screen.Scaling * ScreenShare), Math.Floor(area.Height / screen.Scaling * ScreenShare));
        Width = _outer.Value.Width;
        Height = _outer.Value.Height;
    }

    /// <summary>
    /// Размер окна на WPF — вместе с рамкой и заголовком, а Width/Height в Avalonia — только область содержимого.
    /// Рамка видна лишь после показа: тогда убавляем область на её толщину, чтобы окно целиком заняло долю экрана, и
    /// возвращаем окно на то же место по центру.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_outer is not { } outer || FrameSize is not { } frame) return;
        var (dx, dy) = (frame.Width - ClientSize.Width, frame.Height - ClientSize.Height);
        if (dx < 1 && dy < 1) return; // рамки нет (Wayland, сервер композиции рисует её сам): размер уже верный
        Width = outer.Width - dx;
        Height = outer.Height - dy;
        var shift = (frame - outer) / 2 * RenderScaling;
        Position = new PixelPoint(Position.X + (int)Math.Round(shift.Width), Position.Y + (int)Math.Round(shift.Height));
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}

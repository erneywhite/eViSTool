using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Path = Avalonia.Controls.Shapes.Path;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// «Окно» в подложку боковой панели (docs/design.md, раздел 5, «Окно в подложку»): элемент показывает то, что лежало бы
/// под ним, если бы боковая панель тянулась до этого места окна. С фоном из игры — кусок той же картинки: тот же Bitmap,
/// что у Image#Backdrop главного окна (заново не грузится), с той же геометрией растяжения и обрезки, и над ним та же
/// вуаль. Без картинки — цвет Side и «горизонтали»: тот же узор, выровненный по окну (линия, начатая под панелью,
/// продолжается здесь), с тем же сдвигом плавания — RenderTransform у копии линий общий с линиями панели
/// (<see cref="ContourDrift"/>), поэтому плывут они синхронно и стоят при тех же условиях.
/// <para>Откуда брать картинку и линии — наследуемые <see cref="PictureProperty"/> и <see cref="LinesProperty"/>: их
/// ставит главное окно на Root, и элемент находит их на любой странице.</para>
/// <para>Положение пересчитывается при раскладке (размер окна, прокрутка страницы) и при смене картинки, не на каждом
/// кадре. Считается оно по раскладке (Bounds), без RenderTransform: проявление страницы сдвигает её на 6 px на четверть
/// секунды, и пересчёт посреди проявления оставил бы картинку сдвинутой после него. Рисует элемент только в своих
/// границах (ClipToBounds), но перерисованное за кадр Avalonia сводит в один прямоугольник: пока линии плывут, это
/// прямоугольник от боковой панели до правого края элемента на всю высоту окна, а не два отдельных (цена — там же в
/// docs/design.md). С картинкой линии не двигаются, и перерисовки от плавания нет.</para>
/// </summary>
public sealed class BackdropWindow : Control
{
    /// <summary>Картинка фона на всё окно (Image#Backdrop главного окна). Наследуется.</summary>
    public static readonly AttachedProperty<Image?> PictureProperty =
        AvaloniaProperty.RegisterAttached<BackdropWindow, Control, Image?>("Picture", inherits: true);

    /// <summary>«Горизонтали» под боковой панелью (Path#Contours главного окна): начало узора, цвет и сдвиг. Наследуется.</summary>
    public static readonly AttachedProperty<Path?> LinesProperty =
        AvaloniaProperty.RegisterAttached<BackdropWindow, Control, Path?>("Lines", inherits: true);

    /// <summary>Вуаль над картинкой — как у боковой панели (Forest.SideVeil).</summary>
    public static readonly StyledProperty<IBrush?> VeilProperty = AvaloniaProperty.Register<BackdropWindow, IBrush?>(nameof(Veil));

    /// <summary>Цвет под линиями, когда картинки нет (Forest.Side).</summary>
    public static readonly StyledProperty<IBrush?> BackgroundProperty = Border.BackgroundProperty.AddOwner<BackdropWindow>();

    public static Image? GetPicture(Control element) => element.GetValue(PictureProperty);
    public static void SetPicture(Control element, Image? value) => element.SetValue(PictureProperty, value);
    public static Path? GetLines(Control element) => element.GetValue(LinesProperty);
    public static void SetLines(Control element, Path? value) => element.SetValue(LinesProperty, value);

    public IBrush? Veil { get => GetValue(VeilProperty); set => SetValue(VeilProperty, value); }
    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    static BackdropWindow() => AffectsRender<BackdropWindow>(VeilProperty, BackgroundProperty);

    // копия линий панели: начало узора ставит на место _place (обёртка), плавание — общий сдвиг у самих линий
    private readonly Path _lines = new()
    {
        Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
    };
    private readonly TranslateTransform _place = new();
    private readonly Panel _linesHost;

    private Image? _picture; // на что подписан
    private Path? _side;
    private Shown? _shown;   // какую картинку и куда рисовать; null — картинки нет, видны линии

    /// <summary>Картинка и её место: часть From картинки рисуется в прямоугольник To своих координат.</summary>
    internal sealed record Shown(IImage Source, Rect From, Rect To, BitmapInterpolationMode Mode);

    public BackdropWindow()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
        _linesHost = new Panel { RenderTransform = _place, IsVisible = false, Children = { _lines } };
        VisualChildren.Add(_linesHost);
        LogicalChildren.Add(_linesHost);
    }

    /// <summary>Для тестов: что показано вместо линий; null — линии.</summary>
    internal Shown? ShownPicture => _shown;

    /// <summary>Для тестов: копия линий панели и где у неё начало узора (в своих координатах).</summary>
    internal Path LinesCopy => _lines;
    internal bool LinesShown => _linesHost.IsVisible;
    internal Vector LinesOrigin => new(_place.X, _place.Y);

    protected override Size MeasureOverride(Size availableSize)
    {
        // свой размер даёт родитель (растянут на карточку); узор шире — его обрезает ClipToBounds
        _linesHost.Measure(Size.Infinity);
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _linesHost.Arrange(new Rect(_linesHost.DesiredSize));
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (_shown is { } shown)
        {
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = shown.Mode }))
                context.DrawImage(shown.Source, shown.From, shown.To);
            if (Veil is { } veil) context.FillRectangle(veil, bounds);
        }
        else if (Background is { } background) context.FillRectangle(background, bounds);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LayoutUpdated += OnLayoutUpdated;
        Watch();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        LayoutUpdated -= OnLayoutUpdated;
        Unwatch();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if ((change.Property == PictureProperty || change.Property == LinesProperty) && this.IsAttachedToVisualTree())
        {
            Unwatch();
            Watch();
        }
    }

    private void Watch()
    {
        _picture = GetPicture(this);
        _side = GetLines(this);
        if (_picture is not null) _picture.PropertyChanged += OnPictureChanged;
        if (_side is not null) _side.PropertyChanged += OnSideChanged;
        CopyLines();
        Place();
    }

    private void Unwatch()
    {
        if (_picture is not null) _picture.PropertyChanged -= OnPictureChanged;
        if (_side is not null) _side.PropertyChanged -= OnSideChanged;
        _picture = null;
        _side = null;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => Place();

    private void OnPictureChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Image.SourceProperty || e.Property == Image.StretchProperty || e.Property == IsVisibleProperty) Place();
    }

    private void OnSideChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Shape.StrokeProperty || e.Property == Shape.StrokeThicknessProperty || e.Property == OpacityProperty
            || e.Property == RenderTransformProperty)
            CopyLines();
    }

    // вид линий — как у панели; RenderTransform — тот же объект: его X двигает ContourDrift
    private void CopyLines()
    {
        _lines.Stroke = _side?.Stroke;
        _lines.StrokeThickness = _side?.StrokeThickness ?? 0;
        _lines.Opacity = _side?.Opacity ?? 1;
        _lines.RenderTransform = _side?.RenderTransform;
    }

    /// <summary>Поставить картинку и линии по нынешней раскладке (зовётся после каждого её прохода; тестам — и напрямую).</summary>
    internal void Place()
    {
        if (TopLevel.GetTopLevel(this) is not { } top || Origin(this, top) is not { } me) return;
        // страница спрятана (открыта другая) — считать незачем: раскладки у неё нет, место прежнее
        for (Visual? v = this; v is not null && v != top; v = v.GetVisualParent())
            if (!v.IsVisible) return;

        // картинка — как её рисует Image.Render: масштаб по Stretch во весь элемент, по центру, лишнее обрезано
        Shown? shown = null;
        if (_picture is { Source: { } source, IsVisible: true } picture && Origin(picture, top) is { } at
            && picture.Bounds is { Width: > 0, Height: > 0 } && source.Size is { Width: > 0, Height: > 0 })
        {
            var view = new Rect(picture.Bounds.Size);
            var scale = picture.Stretch.CalculateScaling(view.Size, source.Size, picture.StretchDirection);
            var to = view.CenterRect(new Rect(source.Size * scale)).Intersect(view);
            var from = new Rect(source.Size).CenterRect(new Rect(to.Size / scale));
            shown = new Shown(source, from, to.Translate(at - me), RenderOptions.GetBitmapInterpolationMode(picture));
        }
        if (shown != _shown)
        {
            _shown = shown;
            InvalidateVisual();
        }

        // линии: начало узора — там же, где у линий панели; узора — на всю ширину элемента при любом сдвиге
        _linesHost.IsVisible = shown is null && _side is not null;
        if (_linesHost.IsVisible && Origin(_side!, top) is { } start)
        {
            var origin = start - me;
            _place.X = origin.X;
            _place.Y = origin.Y;
            var data = ContourLines.Strip(Bounds.Width - origin.X);
            if (!ReferenceEquals(_lines.Data, data)) _lines.Data = data;
        }
    }

    /// <summary>
    /// Где лежит элемент в окне — по раскладке: сумма Bounds до корня, без RenderTransform (проявление страницы — временный
    /// сдвиг). Прокрутка в Avalonia — тоже раскладка (ScrollContentPresenter ставит содержимое со смещением). Не в окне — null.
    /// </summary>
    internal static Point? Origin(Visual visual, Visual root)
    {
        double x = 0, y = 0;
        for (var v = visual; v != root; v = v.GetVisualParent())
        {
            if (v is null) return null;
            x += v.Bounds.X;
            y += v.Bounds.Y;
        }
        return new Point(x, y);
    }
}

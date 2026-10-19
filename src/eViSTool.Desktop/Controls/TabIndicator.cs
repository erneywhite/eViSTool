using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Полоска выбранной вкладки — одна на весь ряд (docs/design.md, раздел 3): при смене вкладки она едет к новой и
/// подстраивает ширину под неё за <see cref="MoveDuration"/>, кривая «торможение». Кладётся последним ребёнком в
/// панель-обёртку вокруг ряда вкладок; вкладки — все RadioButton среди остальных детей этой панели (на любой глубине),
/// выбранная — отмеченная и видимая.
/// <code>
/// &lt;Panel&gt;
///     &lt;DockPanel&gt; …вкладки… &lt;/DockPanel&gt;
///     &lt;ctl:TabIndicator Background="{DynamicResource Forest.Accent}" /&gt;
/// &lt;/Panel&gt;
/// </code>
/// <para>Полоска лежит на линии ряда: нижний край — на <see cref="Drop"/> ниже вкладки, то есть на нижней рамке в 1 px,
/// которая проведена под рядом. Положение и ширину берёт из раскладки вкладок (Bounds), а двигает только
/// RenderTransform — сдвиг и масштаб по горизонтали от левого края: в раскладке у неё постоянная ширина
/// <see cref="BaseWidth"/> (и нулевой размер для панели: отрицательные поля), и раскладка ни на одном кадре не
/// пересчитывается. Высота — 2 px.</para>
/// <para>Едет, только когда сменилась выбранная вкладка. Первый показ, вкладка поменяла место или ширину (сменился язык,
/// число на вкладке, ширина окна), ряд был спрятан — полоска сразу встаёт на место. Системное «без анимаций»
/// (<see cref="Motion.EnabledProperty"/>) — тоже сразу. Ни одна вкладка не выбрана — полоски нет.</para>
/// </summary>
public sealed class TabIndicator : Border
{
    /// <summary>За сколько полоска доезжает до новой вкладки (раздел 3: заметное перемещение — как появление, 250 мс).</summary>
    public static readonly TimeSpan MoveDuration = TimeSpan.FromMilliseconds(250);

    /// <summary>Ширина полоски в раскладке, px; на экране её задаёт масштаб.</summary>
    public const double BaseWidth = 100;

    /// <summary>На сколько нижний край полоски ниже вкладки, px: на линии ряда (рамка 1 px под вкладками).</summary>
    public const double Drop = 1;

    private readonly List<RadioButton> _tabs = [];
    private RadioButton? _shown;  // вкладка, под которой полоска стоит или к которой едет
    private Rect _target;         // где полоска должна стоять (в координатах панели-обёртки)
    private CancellationTokenSource? _move;
    private bool _pending;

    public TabIndicator()
    {
        Height = 2;
        Width = BaseWidth;
        // места в раскладке не просит: ряд из пары коротких вкладок не станет шире из-за полоски
        Margin = new Thickness(0, 0, -BaseWidth, -2);
        CornerRadius = new CornerRadius(1);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        RenderTransformOrigin = RelativePoint.TopLeft;
        IsHitTestVisible = false;
        IsVisible = false; // до первой раскладки ряда не знаем, где встать
        // раскладка прошла (сменился язык, число на вкладке, ширина окна, страницу показали): встать на место. Событие
        // приходит после каждого прохода раскладки окна — проверка дешёвая, а без перемен ничего не трогается
        LayoutUpdated += (_, _) => Update();
    }

    /// <summary>Вкладка, под которой полоска стоит или к которой едет; null — полоски нет.</summary>
    public RadioButton? Tab => _shown;

    /// <summary>Едет ли полоска к вкладке сейчас.</summary>
    public bool IsMoving => _move is not null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _tabs.Clear();
        // shortcut: вкладки ищутся, когда ряд входит в окно; вкладку, добавленную позже (ряд из ItemsControl), полоска не
        // увидит. Переделать (следить за детьми панели), если такой ряд появится
        if (this.GetVisualParent() is { } host)
            _tabs.AddRange(host.GetVisualDescendants().OfType<RadioButton>());
        foreach (var tab in _tabs) tab.PropertyChanged += OnTabChanged;
        _shown = null; // снова в окне — первый показ, без движения
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        foreach (var tab in _tabs) tab.PropertyChanged -= OnTabChanged;
        _tabs.Clear();
        StopMove();
        _shown = null;
    }

    // смена отметки раскладку не трогает — её ловим здесь. Привязанные к одному значению вкладки (фильтр «Моих модов»)
    // пересчитываются по одной: старая уже снята, новая ещё нет. Поэтому решаем, куда ехать, один раз — после всех, до
    // кадра
    private void OnTabChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ToggleButton.IsCheckedProperty || _pending) return;
        _pending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pending = false;
            Update();
        }, DispatcherPriority.Normal);
    }

    private void Update()
    {
        // ряд не виден (страница спрятана): двигать нечего, а когда его покажут — встать сразу
        if (this.GetVisualParent() is not { } host || !host.IsEffectivelyVisible || _tabs.Count == 0)
        {
            Forget();
            return;
        }
        var tab = _tabs.FirstOrDefault(t => t.IsChecked == true && t.IsEffectivelyVisible);
        if (tab is null || OffsetIn(tab, host) is not { } at)
        {
            Forget();
            IsVisible = false;
            return;
        }
        var target = new Rect(at.X, at.Y + tab.Bounds.Height + Drop - Height, tab.Bounds.Width, Height);
        if (tab == _shown && target == _target) return;

        // едем только к другой вкладке и только от видной полоски; та же вкладка на новом месте — сразу туда
        var move = tab != _shown && _shown is not null && IsVisible && Motion.GetEnabled(this)
                   && TopLevel.GetTopLevel(this) is { IsVisible: true };
        _shown = tab;
        _target = target;
        if (move) MoveTo(target);
        else JumpTo(target);
    }

    private void Forget()
    {
        StopMove();
        _shown = null;
    }

    /// <summary>Левый верхний угол вкладки в панели-обёртке — по раскладке, без RenderTransform по дороге.</summary>
    private static Point? OffsetIn(Visual tab, Visual host)
    {
        var at = new Point();
        for (var v = tab; v != host; v = v.GetVisualParent())
        {
            if (v is null) return null;
            at += v.Bounds.Position;
        }
        return at;
    }

    private static TransformOperations Place(Rect rect)
    {
        // у Avalonia операции применяются по порядку: сначала растянуть от левого края, потом сдвинуть
        var builder = TransformOperations.CreateBuilder(2);
        builder.AppendScale(rect.Width / BaseWidth, 1);
        builder.AppendTranslate(rect.X, rect.Y);
        return builder.Build();
    }

    private void JumpTo(Rect target)
    {
        StopMove();
        RenderTransform = Place(target);
        IsVisible = true;
    }

    private void MoveTo(Rect target)
    {
        // откуда — с того места, где полоска сейчас на экране: прежний переезд мог не доехать
        var from = RenderTransform as TransformOperations;
        StopMove();
        var to = Place(target);
        RenderTransform = to; // где встанет в конце: анимация своих значений не оставляет
        if (from is null)
        {
            IsVisible = true;
            return;
        }
        var run = new CancellationTokenSource();
        _move = run;
        _ = Play(from, to, run);
    }

    private void StopMove()
    {
        if (_move is not { } run) return;
        _move = null;
        run.Cancel();
    }

    private async Task Play(TransformOperations from, TransformOperations to, CancellationTokenSource run)
    {
        try
        {
            await new Animation
            {
                Duration = MoveDuration,
                Easing = new SplineEasing(0.2, 0.7, 0.2, 1),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { Shift(from) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { Shift(to) } },
                },
            }.RunAsync(this, run.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // полоска — украшение: не доехала — стоит на месте (RenderTransform уже конечный), окно работает дальше
            Platform.CrashLog.Write(ex);
        }
        finally
        {
            if (_move == run) _move = null;
            run.Dispose();
        }
    }

    // своего аниматора для RenderTransform у анимаций Avalonia нет (переходам он есть — TransformOperationsTransition)
    private static Setter Shift(TransformOperations value)
    {
        var setter = new Setter(RenderTransformProperty, value);
        Animation.SetAnimator(setter, new TransformOperationsAnimator());
        return setter;
    }

    private sealed class TransformOperationsAnimator : InterpolatingAnimator<ITransform>
    {
        public override ITransform Interpolate(double progress, ITransform oldValue, ITransform newValue) =>
            TransformOperations.Interpolate(oldValue as TransformOperations ?? TransformOperations.Identity,
                newValue as TransformOperations ?? TransformOperations.Identity, progress);
    }
}

using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Движение интерфейса (docs/design.md, раздел 3).
/// <para><c>ctl:Motion.RevealChildren="True"</c> — на панели, где из нескольких детей виден один (страницы окна, вкладки
/// раздела): ребёнок, который стал видимым, проявляется — прозрачность 0 → 1 и сдвиг на 6 px снизу за 250 мс, кривая
/// «торможение». Спрятанный уходит сразу. Пока окно не показано (первый показ при запуске) или сама панель не видна,
/// проявления нет: показывать нечего.</para>
/// <para><c>Motion.Enabled</c> — наследуемое «можно двигаться». В системе выключены анимации — Themes/ForestStill.axaml
/// ставит окнам False: переходы темы мгновенные, проявление — сразу, без движения. Анимации по кругу в стилях
/// (Style.Animations) включаются только условием <c>[(ctl|Motion.Enabled)=True]</c> — тогда и они стоят.</para>
/// Двигаются только прозрачность и RenderTransform: раскладка на каждом кадре не пересчитывается.
/// </summary>
public static class Motion
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<Visual, bool>("Enabled", typeof(Motion), defaultValue: true, inherits: true);

    public static readonly AttachedProperty<bool> RevealChildrenProperty =
        AvaloniaProperty.RegisterAttached<Panel, bool>("RevealChildren", typeof(Motion));

    /// <summary>Длительность проявления (раздел 3: появление страницы, содержимого вкладки).</summary>
    public static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(250);

    /// <summary>Сдвиг, с которого новое содержимое поднимается на место, в px.</summary>
    public const double RevealShift = 6;

    // проявление, которое ещё идёт: элемент спрятали и снова показали раньше конца — прежнее обрывается
    private static readonly ConditionalWeakTable<Visual, CancellationTokenSource> Running = new();

    static Motion()
    {
        // один обработчик на всю программу, а не подписка на каждого ребёнка: стал видимым ребёнок панели с
        // RevealChildren — проявить
        Visual.IsVisibleProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.GetNewValue<bool>() && control.GetVisualParent() is Panel panel && GetRevealChildren(panel)) Reveal(control);
        });
    }

    public static bool GetEnabled(Visual visual) => visual.GetValue(EnabledProperty);
    public static void SetEnabled(Visual visual, bool value) => visual.SetValue(EnabledProperty, value);
    public static bool GetRevealChildren(Panel panel) => panel.GetValue(RevealChildrenProperty);
    public static void SetRevealChildren(Panel panel, bool value) => panel.SetValue(RevealChildrenProperty, value);

    /// <summary>
    /// Проявить только что показанный элемент. Ничего не делает, если его не видно (родитель спрятан, окно ещё не
    /// показано) или движение выключено — тогда элемент просто есть, сразу целиком.
    /// </summary>
    public static void Reveal(Visual target)
    {
        if (Running.TryGetValue(target, out var previous))
        {
            Running.Remove(target);
            previous.Cancel();
        }
        // окно ещё не показано (первый показ при запуске) или не в окне — проявлять некому. Видимость самого элемента
        // уже новая, а родителя от этой смены не зависит: по нему и судим, спрятан ли раздел
        if (!target.IsVisible || TopLevel.GetTopLevel(target) is not { IsVisible: true }
            || target.GetVisualParent() is not { IsEffectivelyVisible: true } || !GetEnabled(target)) return;

        var run = new CancellationTokenSource();
        Running.AddOrUpdate(target, run);
        _ = Play(target, run);
    }

    // анимация, а не переходы: она не оставляет элементу своих значений — по окончании прозрачность и RenderTransform
    // снова те, что дают разметка и стили. Запускается сразу, в том же вызове: первый кадр после показа уже прозрачный
    private static async Task Play(Visual target, CancellationTokenSource run)
    {
        try { await RevealAnimation().RunAsync(target, run.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // проявление — украшение: не вышло — элемент просто показан, окно работает дальше
            Platform.CrashLog.Write(ex);
        }
        finally
        {
            if (Running.TryGetValue(target, out var current) && current == run) Running.Remove(target);
            run.Dispose();
        }
    }

    private static Animation RevealAnimation()
    {
        var below = Shift(TransformOperations.Parse(string.Create(CultureInfo.InvariantCulture, $"translateY({RevealShift}px)")));
        var inPlace = Shift(TransformOperations.Parse("translateY(0px)"));
        return new Animation
        {
            Duration = RevealDuration,
            Easing = new SplineEasing(0.2, 0.7, 0.2, 1),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 0d), below } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1d), inPlace } },
            },
        };
    }

    // своего аниматора для RenderTransform у анимаций Avalonia нет (переходам он есть — TransformOperationsTransition):
    // кадр сдвига задаём со своим, который смешивает TransformOperations (им же пользуется PopupMotion)
    internal static Setter Shift(TransformOperations value)
    {
        var setter = new Setter(Visual.RenderTransformProperty, value);
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

using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Выпадающие меню и списки проявляются при открытии (docs/design.md, раздел 3): содержимое Popup — прозрачность 0 → 1 и
/// сдвиг на <see cref="Shift"/> px сверху вниз за <see cref="Duration"/>, кривая «торможение». Раскрывшееся вверх (над
/// полем, у нижнего края экрана) едет снизу вверх. Закрытие — сразу: Popup просто убирает окно, а начатое проявление
/// обрывается.
/// <para><c>ctl:PopupMotion.Reveal="True"</c> — у каких Popup так (Themes/ForestPopups.axaml: у всех). Запуск — по событию
/// Opened, на каждое открытие: окно меню уже показано и поставлено на место (видно, куда раскрылось), а кадра ещё не
/// было — первый кадр уже прозрачный.</para>
/// <para>В системе выключены анимации (<see cref="Motion.EnabledProperty"/>) — меню сразу на месте, без движения.
/// Двигаются только прозрачность и RenderTransform, размер окна меню и раскладка не меняются; по окончании своих
/// значений не остаётся.</para>
/// </summary>
public static class PopupMotion
{
    public static readonly AttachedProperty<bool> RevealProperty =
        AvaloniaProperty.RegisterAttached<Popup, bool>("Reveal", typeof(PopupMotion));

    /// <summary>Длительность проявления: чуть короче наведения (180 мс) — меню открывают, чтобы сразу выбрать.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(160);

    /// <summary>Сдвиг, с которого меню доезжает до места, в px.</summary>
    public const double Shift = 4;

    // проявление, которое ещё идёт: меню закрыли или открыли снова раньше конца — прежнее обрывается
    private static readonly ConditionalWeakTable<Control, CancellationTokenSource> Running = new();

    static PopupMotion()
    {
        RevealProperty.Changed.AddClassHandler<Popup>((popup, e) =>
        {
            popup.Opened -= OnOpened;
            popup.Closed -= OnClosed;
            if (!e.GetNewValue<bool>()) return;
            popup.Opened += OnOpened;
            popup.Closed += OnClosed;
        });
    }

    public static bool GetReveal(Popup popup) => popup.GetValue(RevealProperty);
    public static void SetReveal(Popup popup, bool value) => popup.SetValue(RevealProperty, value);

    private static void OnOpened(object? sender, EventArgs e)
    {
        if (sender is not Popup { Child: { } child } popup) return;
        Stop(child);
        if (!Motion.GetEnabled(child)) return;
        var run = new CancellationTokenSource();
        Running.AddOrUpdate(child, run);
        _ = Play(child, OpensUp(popup, child) ? Shift : -Shift, run);
    }

    private static void OnClosed(object? sender, EventArgs e)
    {
        if (sender is Popup { Child: { } child }) Stop(child);
    }

    private static void Stop(Control child)
    {
        if (!Running.TryGetValue(child, out var previous)) return;
        Running.Remove(child);
        previous.Cancel();
    }

    /// <summary>
    /// Раскрылся ли Popup вверх: середина меню выше середины того, к чему оно привязано (поле, кнопка, пункт меню), — так
    /// видно и переворот у края экрана. У контекстного меню привязка — точка нажатия, а её окно не знает: оно всегда едет
    /// сверху вниз. Меню, которому место ещё не дали, судим по тому, куда его просили раскрыть.
    /// </summary>
    internal static bool OpensUp(Popup popup, Control child)
    {
        if (popup.Placement == PlacementMode.Pointer) return false;
        var target = popup.PlacementTarget ?? popup.FindLogicalAncestorOfType<Control>();
        if (target is not null && IsPlaced(child))
        {
            try { return Middle(child) < Middle(target); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // привязка ушла из окна, пока меню открывалось: судим по заказанному месту
            }
        }
        return popup.Placement is PlacementMode.Top or PlacementMode.TopEdgeAlignedLeft or PlacementMode.TopEdgeAlignedRight
            or PlacementMode.LeftEdgeAlignedBottom or PlacementMode.RightEdgeAlignedBottom;

        static double Middle(Visual visual) =>
            (visual.PointToScreen(default).Y + visual.PointToScreen(new Point(0, visual.Bounds.Height)).Y) / 2.0;
    }

    // своё окно у меню (Windows, Linux) к Opened уже стоит на месте. Меню в слое окна (OverlayPopupHost: так без своих
    // окон, например в тестах) место получает только к кадру — до того Canvas.Top у хозяина не задан
    private static bool IsPlaced(Control child) =>
        child.FindAncestorOfType<OverlayPopupHost>() is not { } overlay || !double.IsNaN(Canvas.GetTop(overlay));

    // анимация, а не переходы: по окончании своих значений не остаётся — прозрачность и RenderTransform снова те, что
    // дают разметка и стили
    private static async Task Play(Control child, double from, CancellationTokenSource run)
    {
        try { await Reveal(from).RunAsync(child, run.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // проявление — украшение: не вышло — меню просто открыто, окно работает дальше
            Platform.CrashLog.Write(ex);
        }
        finally
        {
            if (Running.TryGetValue(child, out var current) && current == run) Running.Remove(child);
            run.Dispose();
        }
    }

    private static Animation Reveal(double from) => new()
    {
        Duration = Duration,
        Easing = new SplineEasing(0.2, 0.7, 0.2, 1),
        Children =
        {
            new KeyFrame
            {
                Cue = new Cue(0),
                Setters =
                {
                    new Setter(Visual.OpacityProperty, 0d),
                    Motion.Shift(TransformOperations.Parse(string.Create(CultureInfo.InvariantCulture, $"translateY({from}px)"))),
                },
            },
            new KeyFrame
            {
                Cue = new Cue(1),
                Setters = { new Setter(Visual.OpacityProperty, 1d), Motion.Shift(TransformOperations.Parse("translateY(0px)")) },
            },
        },
    };
}

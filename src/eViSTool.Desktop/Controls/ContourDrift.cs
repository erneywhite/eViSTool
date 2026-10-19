using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// «Горизонтали» под боковой панелью медленно плывут влево (docs/design.md, раздел 5, «Заглушка»), почти бесплатно:
/// сдвиг на 1 px раз в <see cref="Interval"/> по таймеру — несколько перерисовок в секунду, а не анимация на каждый кадр.
/// Двигается только RenderTransform линий: раскладка не пересчитывается, а рамка вокруг них с ClipToBounds не пускает
/// перерисовку дальше боковой панели. Через <see cref="ContourLines.Period"/> шагов сдвиг возвращается к нулю: узор с
/// этим периодом повторяется, скачка не видно.
/// <para>Таймер стоит (и не тикает вхолостую), когда двигаться незачем: окно не показано, свёрнуто или не в фокусе,
/// линий не видно (под панелью фон из игры) или в системе выключены анимации (<see cref="Motion.EnabledProperty"/>).
/// После остановки линии продолжают с того же места.</para>
/// </summary>
public sealed class ContourDrift
{
    /// <summary>Как часто линии сдвигаются на 1 px.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(170);

    private readonly Window _window;
    private readonly Visual _lines;
    private readonly TranslateTransform _shift = new();
    private readonly DispatcherTimer _timer = new() { Interval = Interval };
    private int _step;

    public ContourDrift(Window window, Visual lines)
    {
        _window = window;
        _lines = lines;
        lines.RenderTransform = _shift;
        _timer.Tick += (_, _) => Advance();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty || e.Property == WindowBase.IsActiveProperty || e.Property == Window.WindowStateProperty)
                Update();
        };
        // видимость линий меняет стиль (класс backdrop у окна), «можно двигаться» наследуется от окна (Themes/ForestStill.axaml)
        lines.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty || e.Property == Motion.EnabledProperty) Update();
        };
        Update();
    }

    /// <summary>Идёт ли таймер сдвига.</summary>
    public bool IsMoving => _timer.IsEnabled;

    /// <summary>Нынешний сдвиг линий по горизонтали, px: от 0 до −(период − 1).</summary>
    public double Offset => _shift.X;

    private bool ShouldMove =>
        _window.IsVisible && _window.IsActive && _window.WindowState != WindowState.Minimized
        && _lines.IsVisible && Motion.GetEnabled(_lines);

    private void Update()
    {
        if (ShouldMove) _timer.Start();
        else _timer.Stop();
    }

    /// <summary>Сдвинуть линии на 1 px влево; через период — обратно к нулю, что на вид то же самое.</summary>
    internal void Advance()
    {
        _step = (_step + 1) % ContourLines.Period;
        _shift.X = -_step;
    }
}

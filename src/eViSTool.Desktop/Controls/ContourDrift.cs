using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// «Горизонтали» под боковой панелью медленно плывут влево (docs/design.md, раздел 5, «Заглушка») — плавно и почти
/// бесплатно: <see cref="Rate"/> раз в секунду таймер ставит линиям сдвиг по часам, <see cref="Speed"/> px/с. Сдвиг
/// дробный (0,25–0,3 px за шаг): линия переходит по пикселю постепенно, рывков по целому пикселю нет. Через
/// <see cref="ContourLines.Period"/> px сдвиг возвращается к нулю: узор с этим периодом повторяется, шва не видно.
/// <para>На шаге поток окна только меняет X у TranslateTransform линий: раскладка не пересчитывается, а рамка вокруг них
/// с ClipToBounds не пускает перерисовку дальше боковой панели. Но каждый шаг — это кадр окна (перерисовать панель и
/// показать окно), поэтому шагов в секунду ровно столько, чтобы каждый был не больше трети пикселя.</para>
/// <para>Почему не анимация на потоке отрисовки (Composition API, сдвиг Translation): она перерисовывает и показывает
/// окно на каждом кадре экрана, а на мониторе 180 Гц это 13–20 % ядра даже для точки 2 × 2 px. Замер в окне на Windows
/// (RTX 5070, 1400 × 860, по 20 с): прежний шаг на 1 px раз в 170 мс — 2,5–3 % ядра, 20 шагов в секунду — около 3,5 %,
/// 30 — 7–8 %. Таймер окна на Windows округляет срок вверх до шага системных часов 15,6 мс, и вместо 20 выходит около
/// 16 шагов в секунду (0,3 px за шаг); на Linux — 20 (0,25 px).</para>
/// <para>Таймер стоит (и не тикает вхолостую), когда двигаться незачем: окно не показано, свёрнуто или не в фокусе,
/// линий не видно (под панелью фон из игры) или в системе выключены анимации (<see cref="Motion.EnabledProperty"/>).
/// Пока стоит, часы сдвига не идут: потом линии продолжают с того же места.</para>
/// </summary>
public sealed class ContourDrift
{
    /// <summary>Скорость, px/с: чуть медленнее прежних 6 (1 px раз в 170 мс).</summary>
    public const double Speed = 5;

    /// <summary>Сколько раз в секунду таймер обновляет сдвиг (на Windows на деле около 16, см. выше).</summary>
    public const int Rate = 20;

    private readonly Window _window;
    private readonly Visual _lines;
    private readonly TranslateTransform _shift = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.0 / Rate) };
    private double _phase;   // сколько px узора пройдено к последнему запуску, от 0 до периода
    private long _startedAt; // метка Stopwatch последнего запуска

    public ContourDrift(Window window, Visual lines)
    {
        _window = window;
        _lines = lines;
        lines.RenderTransform = _shift;
        _timer.Tick += (_, _) => _shift.X = -PhaseNow();
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

    /// <summary>Нынешний сдвиг линий по горизонтали, px: дробный, от 0 до −период (не включая).</summary>
    public double Offset => _shift.X;

    private bool ShouldMove =>
        _window.IsVisible && _window.IsActive && _window.WindowState != WindowState.Minimized
        && _lines.IsVisible && Motion.GetEnabled(_lines);

    private void Update()
    {
        if (ShouldMove == IsMoving) return;
        if (IsMoving)
        {
            // встать там, где линии сейчас, и не терять пройденного: следующий запуск продолжит отсюда
            _phase = PhaseNow();
            _timer.Stop();
            _shift.X = -_phase;
        }
        else
        {
            _startedAt = Stopwatch.GetTimestamp();
            _timer.Start();
        }
    }

    private double PhaseNow() => IsMoving ? PhaseAfter(_phase, Stopwatch.GetElapsedTime(_startedAt)) : _phase;

    /// <summary>
    /// Сколько px узора пройдено через <paramref name="elapsed"/> движения от <paramref name="phase"/>: по часам, а не по
    /// числу шагов — таймер окна тикает неровно (на Windows шаг 15,6 мс), а скорость от этого не плавает.
    /// </summary>
    internal static double PhaseAfter(double phase, TimeSpan elapsed) => (phase + elapsed.TotalSeconds * Speed) % ContourLines.Period;

    /// <summary>Для тестов: поставить линии на <paramref name="phase"/> px круга; идущее движение продолжится оттуда.</summary>
    internal void Seek(double phase)
    {
        _phase = ((phase % ContourLines.Period) + ContourLines.Period) % ContourLines.Period;
        _startedAt = Stopwatch.GetTimestamp();
        _shift.X = -_phase;
    }
}

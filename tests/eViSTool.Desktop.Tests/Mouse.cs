using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace eViSTool.Desktop.Tests;

/// <summary>Щелчки мышью по элементам окна в тестах — один порядок действий на все окна.</summary>
internal static class Mouse
{
    /// <summary>
    /// Разложить окно и отрисовать кадр. Мышь ищет, куда попала, по последнему отрисованному кадру, а не по раскладке:
    /// если окно сменило размер (появилась строка, ход копирования), а кадр с новой раскладкой не успел — под нагрузкой
    /// нажатие уходило мимо кнопки. Поэтому сначала раскладка и кадр, и только потом точка и нажатие.
    /// </summary>
    public static void Prepare(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Середина элемента в координатах окна (после <see cref="Prepare"/>).</summary>
    public static Point Center(Window window, Visual visual)
    {
        Prepare(window);
        return At(window, visual, null);
    }

    /// <summary>
    /// Щелчок левой кнопкой по элементу: по середине, а при <paramref name="x"/> — на этом отступе от левого края,
    /// посередине по высоте (у переключателей и строк середина может попасть на пустое место). Команда из привязки
    /// выполняется только настоящим нажатием, не событием Click. <paramref name="count"/> 2 — двойной щелчок.
    /// </summary>
    public static void Click(Window window, Visual visual, double? x = null, int count = 1)
    {
        Prepare(window);
        Press(window, At(window, visual, x), count);
    }

    /// <summary>Щелчок левой кнопкой в точке окна (пустое место или заранее посчитанная точка).</summary>
    public static void ClickAt(Window window, Point point, int count = 1)
    {
        Prepare(window);
        Press(window, point, count);
    }

    private static Point At(Window window, Visual visual, double? x) =>
        visual.TranslatePoint(new Point(x ?? visual.Bounds.Width / 2, visual.Bounds.Height / 2), window)!.Value;

    private static void Press(Window window, Point point, int count)
    {
        for (var i = 0; i < count; i++)
        {
            window.MouseDown(point, MouseButton.Left);
            // двойной щелчок срабатывает на втором нажатии — окно может уже закрыться
            if (!window.IsVisible) break;
            window.MouseUp(point, MouseButton.Left);
        }
        Dispatcher.UIThread.RunJobs();
    }
}

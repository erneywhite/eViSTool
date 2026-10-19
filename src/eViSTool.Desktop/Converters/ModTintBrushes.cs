using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using eViSTool.Presentation;

namespace eViSTool.Desktop.Converters;

/// <summary>
/// Оттенок мода (<see cref="ModTint.Hue"/>) → кисть значка с буквами:
/// <c>Background="{Binding Hue, Converter={x:Static conv:ModTintBrushes.Background}}"</c>, буквы — <see cref="Foreground"/>.
/// Кисти неизменяемые и общие на оттенок: строк в таблице много, а оттенков всего 360.
/// </summary>
public static class ModTintBrushes
{
    private static readonly IBrush?[] Backgrounds = new IBrush?[360];
    private static readonly IBrush?[] Foregrounds = new IBrush?[360];

    public static readonly IValueConverter Background = new FuncValueConverter<int, IBrush>(h => Brush(Backgrounds, h, ModTint.Background));
    public static readonly IValueConverter Foreground = new FuncValueConverter<int, IBrush>(h => Brush(Foregrounds, h, ModTint.Foreground));

    private static IBrush Brush(IBrush?[] cache, int hue, Func<int, Rgb> color)
    {
        var h = (hue % 360 + 360) % 360;
        if (cache[h] is { } ready) return ready;
        var c = color(h);
        // гонка двух потоков даст две одинаковые кисти — не страшно, обе верные
        return cache[h] = new ImmutableSolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
    }
}

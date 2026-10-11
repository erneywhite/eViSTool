using Avalonia.Data.Converters;

namespace eViSTool.Desktop.Converters;

/// <summary>Число → видимость: больше нуля — видно (CountToVisibilityConverter окна на WPF).</summary>
public static class Counts
{
    public static readonly IValueConverter Positive = new FuncValueConverter<int, bool>(n => n > 0);
}

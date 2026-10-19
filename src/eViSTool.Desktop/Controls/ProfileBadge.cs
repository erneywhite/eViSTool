using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Значок профиля: плашка цвета (<see cref="Color"/>, токен Forest.Badge.*) со знаком (<see cref="Glyph"/>, контур
/// Badge.* или знак вида — client, server, cloud). Цвет "" — нейтральная плашка; знак "" — плашка без знака (образец
/// цвета в выборе). Скругление и толщина линии — от размера, чтобы значок одинаково смотрелся от 16 до 40 px.
/// </summary>
public sealed class ProfileBadge : Border
{
    public static readonly StyledProperty<string?> ColorProperty = AvaloniaProperty.Register<ProfileBadge, string?>(nameof(Color));
    public static readonly StyledProperty<string?> GlyphProperty = AvaloniaProperty.Register<ProfileBadge, string?>(nameof(Glyph));

    public string? Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public string? Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    private readonly Path _path = new()
    {
        Stretch = Stretch.Uniform, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    public ProfileBadge()
    {
        Width = Height = 28;
        Child = _path;
        _path.Bind(Path.StrokeProperty, this.GetResourceObservable("Forest.Text"));
        Update();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColorProperty || change.Property == GlyphProperty || change.Property == WidthProperty) Update();
    }

    private void Update()
    {
        var size = double.IsNaN(Width) ? 28 : Width;
        CornerRadius = new CornerRadius(size * 0.27);
        Bind(BackgroundProperty, this.GetResourceObservable(Color is { Length: > 0 } c ? "Forest.Badge." + Pascal(c) : "Forest.Raised"));

        _path.Margin = new Thickness(size * 0.22);
        _path.StrokeThickness = Math.Max(1.2, size / 18);
        _path.IsVisible = Glyph is { Length: > 0 };
        if (Glyph is { Length: > 0 } g)
            _path.Bind(Path.DataProperty, this.GetResourceObservable(g switch
            {
                "client" => "Icon.Client",
                "server" => "Icon.Server",
                "cloud" => "Icon.Cloud",
                _ => "Badge." + Pascal(g),
            }));
    }

    private static string Pascal(string name) => char.ToUpperInvariant(name[0]) + name[1..];
}

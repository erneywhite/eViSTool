using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;
using eViSTool.Core.Notifications;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Логотип канала оповещений — тот же, что у самого сервиса, в его цветах: Telegram — голубой круг с белым самолётиком,
/// Discord и ntfy — белый знак на плашке фирменного цвета, как значок их приложений. У вебхука своего логотипа нет —
/// стрелка на нейтральной плашке. Контуры — Simple Icons (CC0), цвета и контуры — токены Brand.* и Logo.* темы.
/// </summary>
public sealed class ChannelLogo : Panel
{
    public static readonly StyledProperty<NotifyKind> KindProperty = AvaloniaProperty.Register<ChannelLogo, NotifyKind>(nameof(Kind));

    public NotifyKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public ChannelLogo()
    {
        Width = Height = 28;
        Rebuild();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty || change.Property == WidthProperty) Rebuild(); // отступы — доля размера
    }

    private void Rebuild()
    {
        Children.Clear();
        var size = double.IsNaN(Width) ? 28 : Width;
        switch (Kind)
        {
            case NotifyKind.Telegram:
                // в контуре самолётик вырезан — под ним белый круг, получается знак как у Telegram
                Children.Add(Brushed(new Ellipse { Margin = new Thickness(size * 0.18) }, Shape.FillProperty, "Forest.OnBrand"));
                Children.Add(Brushed(Logo("Logo.Telegram"), Shape.FillProperty, "Forest.Brand.Telegram"));
                break;
            case NotifyKind.Discord:
                Children.Add(Tile("Forest.Brand.Discord", Brushed(Logo("Logo.Discord", size * 0.2), Shape.FillProperty, "Forest.OnBrand")));
                break;
            case NotifyKind.Ntfy:
                Children.Add(Tile("Forest.Brand.Ntfy", Brushed(Logo("Logo.Ntfy", size * 0.2), Shape.FillProperty, "Forest.OnBrand")));
                break;
            default:
                var arrow = Logo("Icon.External", size * 0.3);
                arrow.Classes.Add("icon"); // линия, как значки окна, а не заливка
                Children.Add(Tile("Forest.Raised", Brushed(arrow, Shape.StrokeProperty, "Forest.Muted")));
                break;
        }
    }

    private Path Logo(string key, double inset = 0)
    {
        // по центру, а не растянут: контур Discord и ntfy шире, чем выше, и Uniform прижал бы его к верху плашки
        var path = new Path
        {
            Stretch = Stretch.Uniform, Margin = new Thickness(inset), Width = double.NaN, Height = double.NaN,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        path.Bind(Path.DataProperty, this.GetResourceObservable(key));
        return path;
    }

    private Border Tile(string background, Control content)
    {
        var tile = new Border { CornerRadius = new CornerRadius(7), Child = content };
        return Brushed(tile, Border.BackgroundProperty, background);
    }

    private T Brushed<T>(T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, this.GetResourceObservable(key));
        return control;
    }
}

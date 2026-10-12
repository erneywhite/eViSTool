using Avalonia;
using Avalonia.Media;

namespace eViSTool.Desktop;

internal static class Program
{
    /// <summary>Шрифт интерфейса — вшитый Noto Sans: одинаковая ширина текста на Windows и Linux.</summary>
    public const string UiFont = "avares://eViSTool/Assets/Fonts#Noto Sans";

    /// <summary>Моноширинный (консоль, версии, пути) — вшитый Cascadia Mono.</summary>
    public const string MonoFont = "avares://eViSTool/Assets/Fonts#Cascadia Mono";

    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = UiFont })
#pragma warning disable AVALONIA_X11_CSD
            // своя шапка главного окна на Linux: без этого X11 не рисует кнопки и рамку сам и ExtendClientArea не действует
            // (у Avalonia 12 опция помечена экспериментальной; на крайний случай — «Системная рамка» в настройках)
            .With(new X11PlatformOptions { EnableDrawnDecorations = true })
#pragma warning restore AVALONIA_X11_CSD
            .LogToTrace();
}

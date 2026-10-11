using Avalonia;
using Avalonia.Media;

namespace eViSTool.Desktop;

internal static class Program
{
    /// <summary>Шрифт интерфейса — вшитый Noto Sans: одинаковая ширина текста на Windows и Linux.</summary>
    public const string UiFont = "avares://eViSTool.Desktop/Assets/Fonts#Noto Sans";

    /// <summary>Моноширинный (консоль, версии, пути) — вшитый Cascadia Mono.</summary>
    public const string MonoFont = "avares://eViSTool.Desktop/Assets/Fonts#Cascadia Mono";

    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = UiFont })
            .LogToTrace();
}

using Avalonia;
using Avalonia.Media;

namespace eViSTool.Desktop;

internal static class Program
{
    /// <summary>Шрифт интерфейса — вшитый Noto Sans: одинаковая ширина текста на Windows и Linux.</summary>
    public const string UiFont = "avares://eViSTool/Assets/Fonts#Noto Sans";

    /// <summary>Моноширинный (консоль, версии, пути) — вшитый Cascadia Mono.</summary>
    public const string MonoFont = "avares://eViSTool/Assets/Fonts#Cascadia Mono";

    /// <summary>
    /// Шрифты окна: по умолчанию Noto Sans, а знаки, которых в нём нет (стрелки → ← ↑ ↓, треугольники ▾ ▲ ▼ ▸, галочка ✓,
    /// ♥ ◆ ● …), — из вшитого же Cascadia Mono, первым запасным, раньше системных шрифтов. Иначе Avalonia ищет такой знак
    /// в системе: на другой машине он другой, а у знаков с вариантом-эмодзи бывает цветной квадрат Segoe UI Emoji, если
    /// тот шрифт уже подгружен (окно показало эмодзи из названия мода). Обычный текст не меняется: запасной шрифт
    /// спрашивают только о знаках, которых нет в основном. Что все знаки текстов окна есть в одном из двух шрифтов,
    /// проверяет FontCoverageTests.
    /// </summary>
    public static FontManagerOptions FontOptions => new()
    {
        DefaultFamilyName = UiFont,
        FontFallbacks = [new FontFallback { FontFamily = new FontFamily(MonoFont) }],
    };

    /// <summary>Замок «одно окно на папку данных»; null — режим снимка (стенду замок не нужен).</summary>
    public static Platform.SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        if (Presentation.Snapshots.FromEnvironment() is null)
        {
            // до интерфейса: второй запуск из той же папки только поднимает открытое окно. 10 с — ждать прежнее окно,
            // которое закрывается после обновления (обычный запуск замок берёт сразу и не ждёт)
            Instance = Platform.SingleInstance.Start(Core.AppPaths.Root, TimeSpan.FromSeconds(10));
            if (Instance is null) return 0;
        }
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Instance?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(FontOptions)
#pragma warning disable AVALONIA_X11_CSD
            // своя шапка главного окна на Linux: без этого X11 не рисует кнопки и рамку сам и ExtendClientArea не действует
            // (у Avalonia 12 опция помечена экспериментальной; на крайний случай — «Системная рамка» в настройках)
            .With(new X11PlatformOptions { EnableDrawnDecorations = true })
#pragma warning restore AVALONIA_X11_CSD
            .LogToTrace();
}

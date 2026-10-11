using System.Globalization;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation;

/// <summary>Снимок окна: какой экран, куда сохранить, какого размера окно и сколько ждать данных.</summary>
/// <param name="Profile">Имя профиля, который сделать активным перед снимком; null — какой был.</param>
public sealed record SnapshotRequest(string Screen, string OutPath, int Width, int Height, TimeSpan Wait, string? Profile = null);

/// <summary>
/// Снимки окна для сверки переноса на Avalonia: окно (на WPF или на Avalonia) запускается с переменной
/// EVISTOOL_SNAPSHOT, переходит на нужный экран, ждёт, сохраняет себя в PNG и закрывается. Оба окна берут одни и те же
/// данные и одну и ту же навигацию отсюда — снимки сравнимы один к одному.
/// Формат: <c>screen=server.config.world;out=C:\snap\a.png;size=1400x860;wait=5;profile=Сервер</c> (size, wait, profile —
/// необязательно).
/// Экраны: mods, mods.history, catalog, modconfig, settings, about, server[.console|players|stats|announcements|
/// config[.general|world|roles|advanced]|schedule|remote|notify].
/// </summary>
public static class Snapshots
{
    public const string Variable = "EVISTOOL_SNAPSHOT";

    public static SnapshotRequest? FromEnvironment() => Parse(Environment.GetEnvironmentVariable(Variable));

    public static SnapshotRequest? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());
        if (!parts.TryGetValue("screen", out var screen) || !parts.TryGetValue("out", out var outPath)) return null;
        var (w, h) = (1400, 860);
        if (parts.TryGetValue("size", out var size) && size.Split('x') is [var sw, var sh]
            && int.TryParse(sw, CultureInfo.InvariantCulture, out var pw) && int.TryParse(sh, CultureInfo.InvariantCulture, out var ph))
            (w, h) = (pw, ph);
        var wait = parts.TryGetValue("wait", out var ws) && double.TryParse(ws, CultureInfo.InvariantCulture, out var sec) ? sec : 4;
        return new SnapshotRequest(screen.ToLowerInvariant(), outPath, w, h, TimeSpan.FromSeconds(wait), parts.GetValueOrDefault("profile"));
    }

    /// <summary>Сделать активным профиль из запроса и перейти на экран. Неизвестное — исключение (лучше упасть, чем снять не то).</summary>
    public static void Navigate(MainViewModel main, SnapshotRequest request)
    {
        if (request.Profile is { } name)
            main.ActiveProfile = main.Profiles.FirstOrDefault(p => p.Name == name)
                                 ?? throw new ArgumentException($"Нет профиля для снимка: {name}");
        var screen = request.Screen;
        var path = screen.Split('.');
        main.SelectedTab = path[0] switch
        {
            "mods" => AppTab.Mods,
            "catalog" => AppTab.Catalog,
            "modconfig" => AppTab.ModConfig,
            "server" => AppTab.Server,
            "settings" => AppTab.Settings,
            "about" => AppTab.About,
            _ => throw new ArgumentException($"Неизвестный экран для снимка: {screen}"),
        };
        if (path[0] == "mods" && path.Length > 1 && path[1] == "history") main.Mods.Filter = ModFilter.History;
        if (path[0] != "server" || path.Length < 2) return;
        main.Server.Tab = Enum.Parse<ServerTab>(path[1], ignoreCase: true);
        if (main.Server.Tab == ServerTab.Config && path.Length > 2)
            main.Server.Config.Tab = Enum.Parse<ConfigTab>(path[2], ignoreCase: true);
    }
}

using System.Globalization;
using System.Reflection;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation;

/// <summary>Снимок окна: какой экран, куда сохранить, какого размера окно и сколько ждать данных.</summary>
/// <param name="Profile">Имя профиля, который сделать активным перед снимком; null — какой был.</param>
public sealed record SnapshotRequest(string Screen, string OutPath, int Width, int Height, TimeSpan Wait, string? Profile = null);

/// <summary>
/// Снимки окна: окно запускается с переменной
/// EVISTOOL_SNAPSHOT, переходит на нужный экран, ждёт, сохраняет себя в PNG и закрывается. Данные и навигацию окно берёт отсюда — снимки воспроизводимы.
/// Формат: <c>screen=server.config.world;out=C:\snap\a.png;size=1400x860;wait=5;profile=Сервер</c> (size, wait, profile —
/// необязательно).
/// Экраны: mods, mods.history, mods.card, catalog, catalog.card, modconfig[.&lt;modid&gt;], profiles, settings, about[.update], server[.console|players|stats|announcements|
/// config[.general|world|roles|advanced]|schedule|remote|notify|backups] (backups — у профиля у хостера).
/// Окна: dialog.&lt;имя&gt; — окно открывает метод с атрибутом <see cref="SnapshotDialogAttribute"/> (по файлу на окно в папке
/// SnapshotDialogs); снимается верхнее окно, а не главное.
/// </summary>
public static class Snapshots
{
    public const string Variable = "EVISTOOL_SNAPSHOT";

    public static SnapshotRequest? FromEnvironment() => Parse(Environment.GetEnvironmentVariable(Variable));

    /// <summary>Снимок окна программы (dialog.*), а не экрана главного окна: снимать верхнее окно.</summary>
    public static bool IsDialog(SnapshotRequest request) => request.Screen.StartsWith("dialog.", StringComparison.Ordinal);

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
        if (path[0] == "dialog")
        {
            OpenDialog(main, screen["dialog.".Length..]);
            return;
        }
        main.SelectedTab = path[0] switch
        {
            "mods" => AppTab.Mods,
            "catalog" => AppTab.Catalog,
            "modconfig" => AppTab.ModConfig,
            "server" => AppTab.Server,
            "profiles" => AppTab.Profiles,
            "settings" => AppTab.Settings,
            "about" => AppTab.About,
            _ => throw new ArgumentException($"Неизвестный экран для снимка: {screen}"),
        };
        if (path[0] == "mods" && path.Length > 1 && path[1] == "history") main.Mods.Filter = ModFilter.History;
        if (path[0] == "mods" && path.Length > 1 && path[1] == "card") main.Mods.Selected = main.Mods.View.Items.FirstOrDefault(); // карточка первого мода
        if (path[0] == "catalog" && path.Length > 1 && path[1] == "card") main.Catalog.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CatalogViewModel.Results) && main.Catalog.Selected is null) main.Catalog.Selected = main.Catalog.Results.FirstOrDefault(); }; // карточка первого найденного: список придёт из сети позже
        if (path[0] == "modconfig" && path.Length > 1) main.ModConfig.Focus(path[1], ""); // открыт первый файл мода с modid path[1], список — весь
        if (path[0] == "about" && path.Length > 1 && path[1] == "update") _ = Task.Delay(request.Wait / 2).ContinueWith(_ => Ui.Post(() => main.About.AvailableUpdate = new Core.AppUpdate.AppRelease { Version = Core.Versioning.ModVersion.ParseOrNull("9.9.9")!, Tag = "v9.9.9", AssetName = "eViSTool-9.9.9-win-x64.zip", AssetUrl = "https://example.invalid/x.zip" })); // найдено обновление: после фоновой проверки при запуске, иначе она его сбросит
        if (path[0] != "server" || path.Length < 2) return;
        main.Server.Tab = Enum.Parse<ServerTab>(path[1], ignoreCase: true);
        if (main.Server.Tab == ServerTab.Config && path.Length > 2)
            main.Server.Config.Tab = Enum.Parse<ConfigTab>(path[2], ignoreCase: true);
    }

    /// <summary>
    /// Открыть окно по имени: метод static void (MainViewModel) с <see cref="SnapshotDialogAttribute"/>. Модальное окно
    /// не вернётся, пока его не закроют, — окно-владелец снимает верхнее окно по таймеру, а потом закрывает программу.
    /// </summary>
    private static void OpenDialog(MainViewModel main, string name)
    {
        var opener = typeof(Snapshots).Assembly.GetTypes()
                         .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                         .FirstOrDefault(m => m.GetCustomAttribute<SnapshotDialogAttribute>()?.Name == name)
                     ?? throw new ArgumentException($"Неизвестное окно для снимка: {name}");
        opener.Invoke(null, [main]);
    }
}

/// <summary>
/// Открывает окно для снимка dialog.&lt;Name&gt;: static void Метод(MainViewModel main) — вызвать окно через
/// AppWindows.Current с примерными данными (или командой модели, которая его открывает). По файлу на окно в папке
/// SnapshotDialogs, чтобы переносы окон не правили один файл.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SnapshotDialogAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

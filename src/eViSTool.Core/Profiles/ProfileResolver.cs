using eViSTool.Core.Game;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using eViSTool.Core.Localization;

namespace eViSTool.Core.Profiles;

/// <summary>Читает из файлов игры, где у профиля лежат моды и какие из них выключены.</summary>
public static class ProfileResolver
{
    // У обычной сборки файл называется clientsettings.json, у некоторых сборок — с префиксом перед этим именем.
    private const string ClientSettingsPattern = "*clientsettings.json";
    public const string ServerConfigName = "serverconfig.json";

    public static ResolvedProfile Resolve(GameProfile profile)
    {
        // удалённый сервер: своих папок нет, моды и версия — у агента на той машине
        if (profile.IsRemote)
            return new ResolvedProfile { Profile = profile }; // моды удалённого сервера окно берёт у его агента

        var warnings = new List<string>();
        var version = string.IsNullOrWhiteSpace(profile.GameDir) ? null : GameInstall.DetectVersion(profile.GameDir);
        if (version is null)
            warnings.Add(Loc.T("profile.gameNotFound",
                          profile.Kind == ProfileKind.Server ? ServerExecutable.FileName : GameLauncher.ClientExeName));

        if (string.IsNullOrWhiteSpace(profile.DataDir) || !Directory.Exists(profile.DataDir))
        {
            warnings.Add(Loc.T("profile.dataNotFound"));
            return new ResolvedProfile { Profile = profile, GameVersion = version, Warnings = warnings };
        }

        var configPath = FindConfig(profile);
        List<string> modPaths = [];
        List<string> disabled = [];

        if (configPath is null)
        {
            warnings.Add(profile.Kind == ProfileKind.Server
                ? Loc.T("profile.noServerConfig")
                : Loc.T("profile.noClientConfig"));
        }
        else
        {
            try
            {
                var root = JObject.Parse(File.ReadAllText(configPath));
                (modPaths, disabled) = profile.Kind == ProfileKind.Server ? ReadServer(root) : ReadClient(root);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                warnings.Add(Loc.T("profile.configReadFailed", Path.GetFileName(configPath), ex.Message));
            }
        }

        var dirs = modPaths
            .Select(p => ResolvePath(p, profile.GameDir))
            .Where(p => p is not null && !IsBuiltInModsDir(p, profile.GameDir))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Игра ищет моды только в папках из своего списка: «<данные>\Mods», которой в списке нет, она не читает
        // (проверено на сервере 1.22.7) — у профиля с общими модами своя пустая Mods в счёт не идёт.
        // Списка ещё нет (игра не запускалась) — она заведёт его с папкой по умолчанию.
        var defaultMods = Path.Combine(profile.DataDir, "Mods");
        if (dirs.Count == 0) dirs.Add(defaultMods);

        return new ResolvedProfile
        {
            Profile = profile,
            GameVersion = version,
            ConfigPath = configPath,
            ModDirs = dirs,
            InstallDir = dirs.FirstOrDefault(Directory.Exists) ?? dirs[0],
            DisabledMods = disabled,
            Warnings = warnings,
        };
    }

    public static string? FindConfig(GameProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.DataDir) || !Directory.Exists(profile.DataDir)) return null;

        if (profile.Kind == ProfileKind.Server)
        {
            var path = Path.Combine(profile.DataDir, ServerConfigName);
            return File.Exists(path) ? path : null;
        }

        // если файлов несколько — тот, который игра трогала последним
        return Directory.EnumerateFiles(profile.DataDir, ClientSettingsPattern)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static (List<string> ModPaths, List<string> Disabled) ReadClient(JObject root)
    {
        var lists = root["stringListSettings"] as JObject;
        return (Strings(lists?["modPaths"]), Strings(lists?["disabledMods"]));
    }

    private static (List<string> ModPaths, List<string> Disabled) ReadServer(JObject root) =>
        (Strings(root["ModPaths"]), Strings(root["WorldConfig"]?["DisabledMods"]));

    private static List<string> Strings(JToken? token) =>
        token is JArray arr ? arr.Where(t => t.Type == JTokenType.String).Select(t => t.ToString()).ToList() : [];

    /// <summary>Относительные пути игра считает от своей папки установки.</summary>
    private static string? ResolvePath(string path, string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = Environment.ExpandEnvironmentVariables(path);
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        return string.IsNullOrWhiteSpace(gameDir) ? null : Path.GetFullPath(Path.Combine(gameDir, path));
    }

    /// <summary>Встроенная папка Mods игры (VSSurvivalMod.dll и т. п.) — её не трогаем.</summary>
    private static bool IsBuiltInModsDir(string dir, string? gameDir)
    {
        if (!string.IsNullOrWhiteSpace(gameDir)
            && string.Equals(Path.GetFullPath(Path.Combine(gameDir, "Mods")).TrimEnd('\\', '/'), dir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return true;
        return File.Exists(Path.Combine(dir, "VSSurvivalMod.dll"));
    }

    /// <summary>
    /// Похоже ли на папку данных выделенного сервера: есть serverconfig*.json, а настроек клиента нет
    /// (у клиента serverconfig.json тоже бывает — от одиночной игры, — но рядом всегда лежат его настройки).
    /// </summary>
    public static bool LooksLikeServerData(string? dataDir) =>
        !string.IsNullOrWhiteSpace(dataDir) && Directory.Exists(dataDir)
        && Directory.EnumerateFiles(dataDir, "serverconfig*.json").Any()
        && !Directory.EnumerateFiles(dataDir, ClientSettingsPattern).Any();

    /// <summary>
    /// Профиль для первого запуска: клиентский на стандартной папке данных, а на машине, где стоит только сервер
    /// (нет Vintagestory.exe или в стандартной папке лежат данные сервера без настроек клиента), — серверный на ней же.
    /// </summary>
    public static GameProfile DefaultProfile(string? gameDir = null)
    {
        gameDir ??= GameInstall.FindGameDir();
        var serverOnly = LooksLikeServerData(GameInstall.DefaultDataDir)
                         || (!string.IsNullOrWhiteSpace(gameDir) && !File.Exists(Path.Combine(gameDir, "Vintagestory.exe"))
                             && File.Exists(ServerExecutable.PathIn(gameDir)));
        return serverOnly
            ? new GameProfile { Name = Loc.T("profile.defaultServerName"), Kind = ProfileKind.Server, GameDir = gameDir, DataDir = GameInstall.DefaultDataDir }
            : DefaultClient(gameDir);
    }

    /// <summary>Профиль клиента по умолчанию — для первого запуска.</summary>
    public static GameProfile DefaultClient(string? gameDir = null) => new()
    {
        Name = Loc.T("profile.defaultClientName"),
        Kind = ProfileKind.Client,
        GameDir = gameDir ?? GameInstall.FindGameDir(),
        DataDir = GameInstall.DefaultDataDir,
    };
}

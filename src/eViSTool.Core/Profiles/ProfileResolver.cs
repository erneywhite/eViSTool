using eViSTool.Core.Game;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using eViSTool.Core.Localization;

namespace eViSTool.Core.Profiles;

/// <summary>Читает из файлов игры, где у профиля лежат моды и какие из них выключены.</summary>
public static class ProfileResolver
{
    // У обычной сборки файл называется clientsettings.json, у некоторых сборок — с префиксом (myclientsettings.json).
    private const string ClientSettingsPattern = "*clientsettings.json";
    public const string ServerConfigName = "serverconfig.json";

    public static ResolvedProfile Resolve(GameProfile profile)
    {
        var warnings = new List<string>();
        var version = string.IsNullOrWhiteSpace(profile.GameDir) ? null : GameInstall.DetectVersion(profile.GameDir);
        if (version is null)
            warnings.Add(Loc.T("profile.gameNotFound",
                          profile.Kind == ProfileKind.Server ? "VintagestoryServer.exe" : "Vintagestory.exe"));

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

        // папка по умолчанию игра использует всегда, даже если её нет в списке
        var defaultMods = Path.Combine(profile.DataDir, "Mods");
        if (!dirs.Contains(defaultMods, StringComparer.OrdinalIgnoreCase)) dirs.Insert(0, defaultMods);

        return new ResolvedProfile
        {
            Profile = profile,
            GameVersion = version,
            ConfigPath = configPath,
            ModDirs = dirs,
            InstallDir = dirs.FirstOrDefault(Directory.Exists) ?? defaultMods,
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

    /// <summary>Профиль клиента по умолчанию — для первого запуска.</summary>
    public static GameProfile DefaultClient(string? gameDir = null) => new()
    {
        Name = Loc.T("profile.defaultClientName"),
        Kind = ProfileKind.Client,
        GameDir = gameDir ?? GameInstall.FindGameDir(),
        DataDir = GameInstall.DefaultDataDir,
    };
}

using eViSTool.Core.Profiles;
using eViSTool.Core.Server.Remote;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Game;

public enum PlayTargetKind { OwnServer, Favorite }

/// <summary>
/// Куда подключиться при запуске игры: свой сервер (профиль eViSTool) или сервер из избранного игры. Key — постоянный
/// ключ для «сервера по умолчанию» у профиля: «own:{id профиля}» или «fav:{имя}|{адрес}».
/// </summary>
public sealed record PlayTarget(string Key, PlayTargetKind Kind, string Name, string Address, string? Password, string? ProfileId = null)
{
    public bool HasPassword => !string.IsNullOrEmpty(Password);
}

/// <summary>
/// Серверы для меню «Играть»: свои (серверные профили eViSTool) и избранное игры. Игра хранит избранное в своём
/// clientsettings.json (stringListSettings.multiplayerservers), строками «имя,адрес[:порт],пароль»; пароль — открытым
/// текстом, так его хранит сама игра.
/// </summary>
public static class PlayTargets
{
    public const int DefaultGamePort = 42420;

    public static string OwnKey(string profileId) => "own:" + profileId;

    /// <summary>Избранное игры профиля (его папки данных). Файла нет или он испорчен — пусто.</summary>
    public static IReadOnlyList<PlayTarget> Favorites(string? dataDir)
    {
        var file = Path.Combine(string.IsNullOrWhiteSpace(dataDir) ? GameInstall.DefaultDataDir : dataDir, "clientsettings.json");
        try
        {
            if (!File.Exists(file)) return [];
            var list = JObject.Parse(File.ReadAllText(file))["stringListSettings"]?["multiplayerservers"] as JArray;
            return [.. (list ?? []).Select(t => t.Type == JTokenType.String ? ParseFavorite(t.Value<string>()!) : null).OfType<PlayTarget>()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>«home,192.168.31.31,» → имя, адрес, пароль. Пароль может содержать запятые — берём всё после второй.</summary>
    public static PlayTarget? ParseFavorite(string line)
    {
        var parts = line.Split(',', 3);
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1])) return null;
        var name = parts[0].Trim();
        var address = parts[1].Trim();
        return new PlayTarget($"fav:{name}|{address}", PlayTargetKind.Favorite, name.Length > 0 ? name : address, address,
            parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null);
    }

    /// <summary>
    /// Свой сервер на этой машине: 127.0.0.1 и порт из его serverconfig.json. Удалённый — адрес из кода подключения
    /// (там же, где агент) и порт игры, который сообщил агент (не знаем — стандартный).
    /// </summary>
    public static PlayTarget? Own(GameProfile server, int? knownGamePort = null)
    {
        if (server.Kind != ProfileKind.Server) return null;
        if (server.IsRemote)
        {
            if (RemoteSecret.Unprotect(server.RemoteCode) is not { } code) return null;
            return new PlayTarget(OwnKey(server.Id), PlayTargetKind.OwnServer, server.Name,
                WithPort(code.Host, knownGamePort ?? DefaultGamePort), null, server.Id);
        }
        return new PlayTarget(OwnKey(server.Id), PlayTargetKind.OwnServer, server.Name,
            WithPort("127.0.0.1", knownGamePort ?? GamePortOf(server.DataDir)), null, server.Id);
    }

    /// <summary>Порт игры из serverconfig.json («Port»); нет файла или поля — стандартный.</summary>
    public static int GamePortOf(string? dataDir)
    {
        try
        {
            var file = dataDir is null ? null : Path.Combine(dataDir, "serverconfig.json");
            if (file is not null && File.Exists(file) && JObject.Parse(File.ReadAllText(file))["Port"] is { Type: JTokenType.Integer } port
                && port.Value<int>() is > 0 and < 65536 and var p)
                return p;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return DefaultGamePort;
    }

    // IPv6 — в квадратных скобках, как ждёт игра
    private static string WithPort(string host, int port) =>
        (host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host) + ":" + port;
}

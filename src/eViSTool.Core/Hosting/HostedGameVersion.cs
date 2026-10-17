using System.Text.RegularExpressions;
using eViSTool.Core.Game;
using eViSTool.Core.Versioning;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Hosting;

/// <summary>
/// Версия игры сервера у хостера. Источники по порядку, берётся первый найденный: указанная вручную → version.txt
/// в папке игры («linux-x64_1.22.7», так у G-Portal) → LastLaunchMods в serverconfig.json (элемент с id «game») →
/// строка «Game Version: v1.22.7» в начале лога сервера → VintagestoryServer.dll (скачивается и читается как у своей
/// установки). Ничего — null: окно попросит указать версию в настройках профиля.
/// </summary>
public static class HostedGameVersion
{
    public const string VersionFile = "version.txt";

    private static readonly Regex LogVersion = new(@"Game Version: v(?<v>\d[^\s]*)", RegexOptions.CultureInvariant);

    /// <param name="profileId">Профиль — для кэша версии из dll; null (проверка подключения) — без кэша.</param>
    public static async Task<(ModVersion? Version, GameVersionSource? Source)> DetectAsync(IRemoteFiles files, HostedConnection settings,
        HostedSnapshot snapshot, string? profileId, CancellationToken ct)
    {
        if (ModVersion.ParseOrNull(settings.GameVersion?.Trim()) is { } manual) return (manual, GameVersionSource.Manual);

        var gameDir = snapshot.Layout.GameDir;
        foreach (var dir in new[] { gameDir, snapshot.Layout.DataDir, snapshot.Layout.Root }.OfType<string>().Distinct())
        {
            if (await snapshot.Listings.FileAsync(RemotePath.Combine(dir, VersionFile), ct).ConfigureAwait(false) is not { Size: < 4096 } file)
                continue;
            if (FromVersionFile(await files.ReadTextAsync(file.Path, ct).ConfigureAwait(false)) is { } v) return (v, GameVersionSource.VersionFile);
        }

        if (snapshot.ConfigJson is { } config && FromLastLaunch(config) is { } launched) return (launched, GameVersionSource.LastLaunch);
        if (FromLog(snapshot.LogHead) is { } logged) return (logged, GameVersionSource.ServerLog);

        if (gameDir is not null
            && await snapshot.Listings.FileAsync(RemotePath.Combine(gameDir, "VintagestoryServer.dll"), ct).ConfigureAwait(false) is { } dll
            && await FromDllAsync(files, dll, profileId, ct).ConfigureAwait(false) is { } fromDll)
            return (fromDll, GameVersionSource.ServerDll);
        return (null, null);
    }

    /// <summary>«linux-x64_1.22.7» → 1.22.7: часть после последнего «_» (или весь текст, если «_» нет).</summary>
    public static ModVersion? FromVersionFile(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Trim().Split('\n')[0].Trim();
        var cut = line.LastIndexOf('_');
        return ModVersion.ParseOrNull(cut >= 0 ? line[(cut + 1)..] : line);
    }

    /// <summary>Версия игры из LastLaunchMods — это JSON-строка со списком <c>{"id":"game","version":"1.22.7"}</c>
    /// (бывает и сразу списком).</summary>
    public static ModVersion? FromLastLaunch(JObject config)
    {
        var token = config["LastLaunchMods"];
        try
        {
            var list = token switch
            {
                JArray array => array,
                JValue { Type: JTokenType.String } s when !string.IsNullOrWhiteSpace((string?)s) => JArray.Parse((string)s!),
                _ => null,
            };
            var game = list?.OfType<JObject>().FirstOrDefault(m => string.Equals((string?)m["id"], "game", StringComparison.OrdinalIgnoreCase));
            return ModVersion.ParseOrNull((string?)game?["version"]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>«Game Version: v1.22.7 (Stable)» из начала лога сервера.</summary>
    public static ModVersion? FromLog(string? log) =>
        log is not null && LogVersion.Match(log) is { Success: true } m ? ModVersion.ParseOrNull(m.Groups["v"].Value) : null;

    /// <summary>Скачать VintagestoryServer.dll во временную папку и прочитать версию; у профиля — с кэшем по размеру и времени.</summary>
    private static async Task<ModVersion?> FromDllAsync(IRemoteFiles files, RemoteEntry dll, string? profileId, CancellationToken ct)
    {
        var key = $"{dll.Size}|{dll.Modified?.Ticks}";
        var state = profileId is null ? null : HostedState.Load(profileId);
        if (state is { DllKey: { } known } && known == key && dll.Modified is not null) return ModVersion.ParseOrNull(state.DllVersion);

        var dir = Path.Combine(AppPaths.Cache, "hosted", "dll-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await using (var file = File.Create(Path.Combine(dir, "VintagestoryServer.dll")))
                await files.DownloadAsync(dll.Path, file, null, ct).ConfigureAwait(false);
            var version = GameInstall.DetectVersion(dir);
            if (state is not null && profileId is not null)
            {
                state.DllKey = key;
                state.DllVersion = version?.ToString();
                state.Save(profileId);
            }
            return version;
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // временная папка в cache — её уберёт «Уборка диска»
            }
        }
    }
}

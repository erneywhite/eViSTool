using eViSTool.Core.Platform;
using Newtonsoft.Json;

namespace eViSTool.Core.Hosting;

/// <summary>
/// Что окно помнит о сервере у хостера между сканами — <c>cache/hosted/&lt;id&gt;/state.json</c>: последний
/// LastLaunchMods (по нему видно, что сервер запускали), что мы сами записали в DisabledMods (проверка «хостер
/// переписал конфиг»), версия из VintagestoryServer.dll по размеру и времени файла. Всё это подсказки: пропал файл —
/// просто узнаем заново. Битый файл — как пустой.
/// </summary>
public sealed class HostedState
{
    public const string FileName = "state.json";

    /// <summary>LastLaunchMods из serverconfig.json при прошлом скане (сырой текст).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string? LastLaunch { get; set; }

    /// <summary>Выключенные моды, которые мы записали сами, и LastLaunchMods в тот момент.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public List<string>? WrittenDisabled { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string? LaunchAtWrite { get; set; }

    /// <summary>Версия из VintagestoryServer.dll и файл, из которого она взята (размер|время).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string? DllKey { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string? DllVersion { get; set; }

    public static string PathFor(string profileId) => Path.Combine(ModInfoCache.DirFor(profileId), FileName);

    public static HostedState Load(string profileId)
    {
        try
        {
            var file = PathFor(profileId);
            if (File.Exists(file) && JsonConvert.DeserializeObject<HostedState>(File.ReadAllText(file)) is { } state) return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // битый — как пустой
        }
        return new HostedState();
    }

    /// <summary>Записать; сбой записи не роняет операцию — это только подсказки.</summary>
    public void Save(string profileId)
    {
        var file = PathFor(profileId);
        var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(tmp, JsonConvert.SerializeObject(this, Formatting.Indented));
            SafeFile.Replace(tmp, file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

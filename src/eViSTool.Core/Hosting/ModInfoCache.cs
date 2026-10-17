using System.Diagnostics.CodeAnalysis;
using eViSTool.Core.Platform;
using Newtonsoft.Json;

namespace eViSTool.Core.Hosting;

/// <summary>
/// Кэш modinfo.json модов сервера у хостера: архив читается по сети один раз на каждую версию файла.
/// Ключ — путь на сервере; запись годна, пока у файла те же размер и время из листинга (время — только на равенство:
/// LIST отдаёт местное время сервера, и после перевода часов архивы просто перечитаются один раз).
/// Файл — <c>cache/hosted/&lt;id профиля&gt;/modinfo.json</c>; битый файл — пустой кэш; запись атомарная.
/// </summary>
public sealed class ModInfoCache
{
    public const string FileName = "modinfo.json";

    private readonly Lock _gate = new();
    private readonly string _file;
    private readonly Dictionary<string, Entry> _entries;
    private bool _dirty;

    private ModInfoCache(string file)
    {
        _file = file;
        _entries = Load(file);
    }

    /// <summary>Папка кэша сервера у хостера: <c>cache/hosted/&lt;id&gt;</c>. Id с путём — исключение: он идёт в путь.</summary>
    public static string DirFor(string profileId) =>
        PathRules.FileIn(Path.Combine(AppPaths.Cache, "hosted"), profileId)
        ?? throw new ArgumentException("bad profile id", nameof(profileId));

    public static ModInfoCache ForProfile(string profileId) => new(Path.Combine(DirFor(profileId), FileName));

    /// <summary>Убрать кэш modinfo профиля (вместе с профилем). Остальное в папке кэша профиля не трогает.</summary>
    public static void Delete(string profileId)
    {
        var file = Path.Combine(DirFor(profileId), FileName);
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // не удалось — останется мусор в cache, его уберёт «Уборка диска»
        }
    }

    /// <summary>Есть запись для этого файла и он не менялся (тот же размер и время). Время неизвестно — промах.</summary>
    public bool TryGet(RemoteEntry file, [NotNullWhen(true)] out CachedModInfo? info)
    {
        lock (_gate)
        {
            if (file.Modified is { } modified && _entries.TryGetValue(file.Path, out var e)
                && e.Size == file.Size && e.Modified == modified)
            {
                info = new CachedModInfo(e.Json, e.Error);
                return true;
            }
        }
        info = null;
        return false;
    }

    public void Put(RemoteEntry file, CachedModInfo info)
    {
        lock (_gate)
        {
            _entries[file.Path] = new Entry { Size = file.Size, Modified = file.Modified, Json = info.ModInfoJson, Error = info.ErrorKey };
            _dirty = true;
        }
    }

    /// <summary>Оставить записи только для этих путей (после скана: файлы, которых больше нет, выбрасываются).</summary>
    public void Retain(IEnumerable<string> paths)
    {
        var keep = paths.ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var gone in _entries.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                _entries.Remove(gone);
                _dirty = true;
            }
        }
    }

    /// <summary>
    /// Записать на диск, если что-то поменялось: через временный файл со своим именем (два окна с одним профилем не
    /// пишут в один и тот же временный файл). Сбой записи не выходит наружу — кэш не должен ронять скан: записи
    /// остаются в памяти и попадут на диск при следующем Save. true — на диске то, что в памяти.
    /// </summary>
    public bool Save()
    {
        string text;
        lock (_gate)
        {
            if (!_dirty) return true;
            text = JsonConvert.SerializeObject(_entries, Formatting.None);
            _dirty = false;
        }
        var tmp = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(tmp, text);
            SafeFile.Replace(tmp, _file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate) _dirty = true;
            try
            {
                File.Delete(tmp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // временный файл остался: вреда нет, следующий Save пишет свой
            }
            return false;
        }
    }

    private static Dictionary<string, Entry> Load(string file)
    {
        try
        {
            if (File.Exists(file)
                && JsonConvert.DeserializeObject<Dictionary<string, Entry>>(File.ReadAllText(file)) is { } read)
                return new Dictionary<string, Entry>(read.Where(kv => kv.Value is not null), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // битый или недоступный кэш — как пустой: архивы перечитаются
        }
        return new Dictionary<string, Entry>(StringComparer.Ordinal);
    }

    private sealed class Entry
    {
        public long Size { get; set; }
        public DateTime? Modified { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string? Json { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string? Error { get; set; }
    }
}

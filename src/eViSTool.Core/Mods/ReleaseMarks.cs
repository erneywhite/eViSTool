using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Platform;
using eViSTool.Core.Versioning;
using Newtonsoft.Json;

namespace eViSTool.Core.Mods;

/// <summary>
/// «Этот файл — релиз ModDB <see cref="ReleaseVersion"/>», хотя в его modinfo.json написано <see cref="FileVersion"/>:
/// автор забыл поднять версию внутри архива. По <see cref="FileName"/> и <see cref="FileVersion"/> отметка узнаёт свой
/// файл: заменили его чем-то другим (другое имя или другая версия внутри) — отметка к нему уже не относится.
/// </summary>
public sealed record ReleaseMark(string FileName, string? FileVersion, long ReleaseId, string ReleaseVersion);

/// <summary>
/// Отметки «установлен файл релиза X» по профилю: файл <c>ModReleases/&lt;профиль&gt;.json</c> в данных eViSTool,
/// modid → <see cref="ReleaseMark"/>. Нужны из-за авторов, которые забывают поднять версию в modinfo.json: в файле
/// релиза 1.8.1 написано 1.8.0, и проверка обновлений, сравнивающая по modinfo, предлагала бы тот же релиз снова и снова.
/// <para>
/// Отметку ставит установка релиза с расхождением версий, снимает — любая другая установка этого мода в профиль (архив,
/// копия из хранилища, релиз без расхождения). Пишет тот, кто ставит мод: окно — для своих профилей и серверов у хостера,
/// агент — для своего сервера (в том числе по просьбе окна). Скан папки модов прикрепляет отметку к моду
/// (<see cref="LocalMod.Release"/>), а <see cref="UpdateChecker"/> сравнивает такой мод по версии релиза. Отметка —
/// подсказка: сбой записи установку не отменяет, без файла всё работает как раньше.
/// </para>
/// </summary>
public static class ReleaseMarks
{
    public static string Dir => Path.Combine(AppPaths.Root, "ModReleases");

    public static string FileFor(string profileId, string? dir = null) => Path.Combine(dir ?? Dir, $"{profileId}.json");

    // окно ставит моды по одному, но фоновые проверки читают файл параллельно с установкой
    private static readonly Lock Gate = new();

    /// <summary>
    /// Версия в файле не та, что у релиза на ModDB. Сравнение по смыслу, через <see cref="ModVersion"/>: «1.8.0», «v1.8.0»
    /// и «1.8» — одно и то же, «1.8.1-rc.1» и «1.8.1» — разное. Не разбирается — сравнивается текст (без «v» и регистра).
    /// У релиза версии нет — сравнивать не с чем, расхождения нет.
    /// </summary>
    public static bool Differs(string? fileVersion, string? releaseVersion)
    {
        if (string.IsNullOrWhiteSpace(releaseVersion)) return false;
        if (ModVersion.ParseOrNull(fileVersion) is { } file && ModVersion.ParseOrNull(releaseVersion) is { } release)
            return file.CompareTo(release) != 0;
        return !string.Equals(Bare(fileVersion), Bare(releaseVersion), StringComparison.OrdinalIgnoreCase);
    }

    private static string Bare(string? version)
    {
        var s = (version ?? "").Trim();
        return s.StartsWith('v') || s.StartsWith('V') ? s[1..] : s;
    }

    /// <summary>Предупреждение для итога установки: версия в файле мода не та, что у релиза на ModDB. null — совпадает.</summary>
    public static string? Warning(string name, string? fileVersion, string? releaseVersion) =>
        !Differs(fileVersion, releaseVersion) ? null
        : string.IsNullOrWhiteSpace(fileVersion) ? Loc.T("dl.versionMissing", name, releaseVersion!.Trim())
        : Loc.T("dl.versionMismatch", name, fileVersion.Trim(), releaseVersion!.Trim());

    /// <summary>Отметка для файла этого релиза; null — релиза нет или версии совпадают, отмечать нечего.</summary>
    public static ReleaseMark? For(ModDbRelease? release, ModInfo info, string fileName) =>
        release is not null && Differs(info.Version, release.ModVersion)
            ? new ReleaseMark(fileName, info.Version, release.ReleaseId, release.ModVersion!.Trim())
            : null;

    /// <summary>Отметки профиля (ключ — modid); файла нет или он испорчен — пусто.</summary>
    public static IReadOnlyDictionary<string, ReleaseMark> Load(string file)
    {
        lock (Gate) return Read(file);
    }

    /// <summary>
    /// После установки мода в профиль: файл — релиз с расхождением версий → отметка, иначе прежняя отметка мода снимается
    /// (файл заменён чем-то другим). <paramref name="fileName"/> — имя, под которым мод лёг в папку модов. Файл отметок
    /// появляется только с первой отметкой: обычные установки его не создают.
    /// </summary>
    public static void Update(string file, ModInfo info, string fileName, ModDbRelease? release)
    {
        var mark = For(release, info, fileName);
        lock (Gate)
        {
            var marks = Read(file);
            if (mark is null && !marks.ContainsKey(info.ModId)) return;
            if (mark is null) marks.Remove(info.ModId);
            else marks[info.ModId] = mark;
            Write(file, marks);
        }
    }

    /// <summary>Отметка этого мода, если она про его нынешний файл: то же имя и та же версия в modinfo.json.</summary>
    public static ReleaseMark? Match(IReadOnlyDictionary<string, ReleaseMark> marks, LocalMod mod) =>
        mod.Info is { } info && marks.TryGetValue(info.ModId, out var mark)
        && string.Equals(mark.FileName, NameOf(mod.Path), StringComparison.OrdinalIgnoreCase)
        && string.Equals(mark.FileVersion?.Trim(), info.Version?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? mark : null;

    /// <summary>
    /// Имя файла по пути любой машины: у удалённого сервера путь — его («/srv/vs/Mods/x.zip» или «C:\VS\Mods\x.zip»),
    /// а Path.GetFileName на Linux обратную косую черту разделителем не считает.
    /// </summary>
    private static string NameOf(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    private static Dictionary<string, ReleaseMark> Read(string file)
    {
        try
        {
            if (File.Exists(file) && JsonConvert.DeserializeObject<Dictionary<string, ReleaseMark>>(File.ReadAllText(file)) is { } read)
            {
                // через индексатор, не конструктор: ключи, что отличаются только регистром (ручная правка файла), не роняют
                // скан исключением — остаётся последний
                var marks = new Dictionary<string, ReleaseMark>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, mark) in read.Where(kv => kv.Value is { FileName.Length: > 0, ReleaseVersion.Length: > 0 }))
                    marks[key] = mark;
                return marks;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // битый или недоступный файл — как пустой: проверка обновлений сравнит по modinfo, как раньше
        }
        return new Dictionary<string, ReleaseMark>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Через временный файл со своим именем (окно и агент на одной машине не пишут в один временный файл).</summary>
    private static void Write(string file, Dictionary<string, ReleaseMark> marks)
    {
        var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(tmp, JsonConvert.SerializeObject(marks, Formatting.Indented));
            SafeFile.Replace(tmp, file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // отметка — подсказка: мод уже поставлен, сбой записи не должен отменять установку
            try { File.Delete(tmp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }
}

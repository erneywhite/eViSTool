using System.Text;
using System.Text.RegularExpressions;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Profiles;

/// <summary>Как редактировать файл: JSON (форма или текст) или просто текст.</summary>
public enum ModConfigKind { Json, Text }

/// <summary>
/// Файл настроек мода в папке ModConfig профиля. <see cref="RelativePath"/> — путь внутри ModConfig («sub/x.json»),
/// по нему же хранятся резервные копии. Мод — тот, чьё имя угадывается по имени файла; не угадался — null.
/// </summary>
public sealed record ModConfigFile(string Path, string RelativePath, ModConfigKind Kind, string? ModId, string? ModName,
    long Size, DateTime ChangedUtc)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Конфиги модов профиля: список с привязкой к модам, чтение, проверка и запись, сброс к стандартным.
/// Перед каждой записью и сбросом прежняя версия уходит в <see cref="ModConfigBackups"/> — «Вернуть как было».
/// </summary>
public static partial class ModConfigs
{
    public const string FolderName = "ModConfig";

    private static readonly HashSet<string> JsonExt = new(StringComparer.OrdinalIgnoreCase) { ".json", ".json5" };
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
        { ".ini", ".cfg", ".conf", ".txt", ".csv", ".yaml", ".yml", ".toml", ".xml", ".properties" };

    public static string DirFor(string dataDir) => System.IO.Path.Combine(dataDir, FolderName);

    /// <summary>Как редактировать файл с таким расширением; null — не текст (картинки, звуки), не показываем.</summary>
    public static ModConfigKind? KindOf(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return JsonExt.Contains(ext) ? ModConfigKind.Json : TextExt.Contains(ext) ? ModConfigKind.Text : null;
    }

    /// <summary>Редактируемые конфиги профиля (текстовые файлы в ModConfig и его подпапках), с модом, если он угадан.</summary>
    public static IReadOnlyList<ModConfigFile> List(string dataDir, IReadOnlyList<LocalMod> mods)
    {
        var dir = DirFor(dataDir);
        if (!Directory.Exists(dir)) return [];
        var keys = ModKeys(mods);
        var list = new List<ModConfigFile>();
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (KindOf(path) is not { } kind) continue;
            var rel = System.IO.Path.GetRelativePath(dir, path).Replace('\\', '/');
            var mod = Match(rel, keys);
            var info = new FileInfo(path);
            list.Add(new ModConfigFile(path, rel, kind, mod?.ModId, mod is null ? null : mod.Name is { Length: > 0 } n ? n : mod.ModId,
                info.Length, info.LastWriteTimeUtc));
        }
        // моды по названию, внутри — по имени файла; неопознанные — в конце
        return list.OrderBy(f => f.ModName is null)
                   .ThenBy(f => f.ModName, StringComparer.CurrentCultureIgnoreCase)
                   .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    /// <summary>
    /// Конфиги, прочитанные не с диска этой машины (листинг FTP сервера у хостера): те же виды файлов, та же привязка
    /// к модам и тот же порядок, что у <see cref="List"/>. Время — как отдал источник.
    /// </summary>
    public static IReadOnlyList<ModConfigEntry> Entries(IEnumerable<(string RelativePath, long Size, DateTime ChangedUtc)> files,
        IReadOnlyList<LocalMod> mods)
    {
        var keys = ModKeys(mods);
        var list = new List<ModConfigEntry>();
        foreach (var (rel, size, changed) in files)
        {
            if (KindOf(rel) is not { } kind) continue;
            var mod = Match(rel, keys);
            list.Add(new ModConfigEntry(rel, kind, mod?.ModId, mod is null ? null : mod.Name is { Length: > 0 } n ? n : mod.ModId, size, changed));
        }
        return list.OrderBy(f => f.ModName is null)
                   .ThenBy(f => f.ModName, StringComparer.CurrentCultureIgnoreCase)
                   .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    private sealed record ModKey(string Key, ModInfo Info);

    /// <summary>Ключи для сравнения: modid и название мода, только буквы и цифры в нижнем регистре.</summary>
    private static List<ModKey> ModKeys(IReadOnlyList<LocalMod> mods) =>
        mods.Where(m => m.Info is not null)
            .SelectMany(m => new[] { Normalize(m.Info!.ModId), Normalize(m.Info.Name) }.Distinct().Select(k => new ModKey(k, m.Info!)))
            .Where(k => k.Key.Length >= 3)
            .ToList();

    /// <summary>
    /// Чей это файл. Имена конфигов авторы пишут как хотят: «AutoLootReforgedConfig.json», «Footprints-Client.json»,
    /// «primitivesurvival5.json», «ProspectTogetherClient-&lt;id мира&gt;.json». Отрезаем идентификаторы, хвосты
    /// Config/Settings/Client/Server и цифры, сравниваем с modid и названием: совпадение или одно начинается с другого.
    /// Из нескольких подходящих — самое длинное совпадение. Файл в подпапке пробуем и по имени папки.
    /// </summary>
    public static ModInfo? MatchMod(string relativePath, IReadOnlyList<LocalMod> mods) => Match(relativePath, ModKeys(mods));

    private static ModInfo? Match(string relativePath, List<ModKey> keys)
    {
        var parts = relativePath.Split('/');
        var candidates = new List<string> { Stem(System.IO.Path.GetFileNameWithoutExtension(parts[^1])) };
        if (parts.Length > 1) candidates.Add(Stem(parts[0]));

        ModKey? best = null;
        var bestLen = 0;
        foreach (var stem in candidates.Where(s => s.Length >= 3))
            foreach (var k in keys)
            {
                var len = stem == k.Key ? k.Key.Length + 1000 // точное совпадение важнее любого «начинается с»
                    : stem.StartsWith(k.Key, StringComparison.Ordinal) && k.Key.Length >= 4 ? k.Key.Length
                    : k.Key.StartsWith(stem, StringComparison.Ordinal) && stem.Length >= 5 ? stem.Length
                    : 0;
                if (len > bestLen) (best, bestLen) = (k, len);
            }
        return best?.Info;
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Guid();

    private static readonly string[] Tails = ["config", "settings", "client", "server", "common", "options"];

    /// <summary>Имя файла без идентификаторов, хвостов Config/Client/… и номеров версий конфига.</summary>
    public static string Stem(string name)
    {
        var s = Normalize(Guid().Replace(name, ""));
        for (var changed = true; changed;)
        {
            changed = false;
            var trimmed = s.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            if (trimmed.Length != s.Length && trimmed.Length >= 3) (s, changed) = (trimmed, true);
            foreach (var t in Tails)
                if (s.Length > t.Length + 2 && s.EndsWith(t, StringComparison.Ordinal))
                    (s, changed) = (s[..^t.Length], true);
        }
        return s;
    }

    private static string Normalize(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>Текст файла как есть: кодировка — по метке (BOM), без метки — UTF-8, если файл им читается.</summary>
    public static string Read(string path) => Decode(File.ReadAllBytes(path), out _);

    /// <summary>
    /// Байты → текст и кодировка, в которой потом сохранять. Без метки и не UTF-8 (файл правили редактором в местной
    /// кодировке) — Latin-1: так каждый байт становится одним знаком, и при сохранении нетронутое возвращается теми
    /// же байтами, а не превращается в «?» (раньше любой такой файл читался как UTF-8 и портился при сохранении).
    /// </summary>
    internal static string Decode(byte[] bytes, out Encoding encoding)
    {
        ReadOnlySpan<byte> span = bytes;
        if (span.StartsWith(Encoding.UTF8.Preamble)) encoding = new UTF8Encoding(true);
        else if (span.StartsWith(Encoding.Unicode.Preamble)) encoding = Encoding.Unicode;
        else if (span.StartsWith(Encoding.BigEndianUnicode.Preamble)) encoding = Encoding.BigEndianUnicode;
        else
        {
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            try
            {
                encoding = new UTF8Encoding(false);
                return strict.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                encoding = Encoding.Latin1;
                return encoding.GetString(bytes);
            }
        }
        return encoding.GetString(bytes, encoding.Preamble.Length, bytes.Length - encoding.Preamble.Length);
    }

    /// <summary>Кодировка для сохранения: прежняя, если новый текст в неё помещается без потерь, иначе UTF-8.</summary>
    internal static Encoding EncodingFor(string path, string text)
    {
        if (!File.Exists(path)) return new UTF8Encoding(false);
        Decode(File.ReadAllBytes(path), out var encoding);
        return encoding.CodePage == Encoding.Latin1.CodePage && encoding.GetString(encoding.GetBytes(text)) != text
            ? new UTF8Encoding(false) : encoding;
    }

    /// <summary>
    /// Ошибка в JSON (строка и позиция) или null, если всё в порядке. Проверка снисходительная, как у игры:
    /// комментарии и хвостовые запятые допустимы (игра читает конфиги тем же Newtonsoft).
    /// </summary>
    public static string? JsonError(string text)
    {
        try
        {
            using var reader = new JsonTextReader(new StringReader(text));
            JToken.ReadFrom(reader, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
            // после значения — только пробелы и комментарии
            while (reader.Read())
                if (reader.TokenType != JsonToken.Comment)
                    return Loc.T("mcfg.jsonTrailing", reader.LineNumber, reader.LinePosition);
            return null;
        }
        catch (JsonReaderException ex)
        {
            return Loc.T("mcfg.jsonError", ex.LineNumber, ex.LinePosition, ex.Message.Split(" Path ")[0].Split(", line ")[0]);
        }
    }

    /// <summary>Разобрать JSON так же снисходительно, как игра (комментарии пропускаются).</summary>
    public static JToken ParseJson(string text)
    {
        // строки вида «2024-01-01» — оставить строками: иначе при записи сменился бы их формат
        using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None };
        return JToken.ReadFrom(reader, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
    }

    /// <summary>В JSON есть комментарии — форма их не сохранит (запись из формы пересобирает файл).</summary>
    public static bool HasComments(string text)
    {
        try
        {
            using var reader = new JsonTextReader(new StringReader(text));
            while (reader.Read())
                if (reader.TokenType == JsonToken.Comment) return true;
            return false;
        }
        catch (JsonReaderException)
        {
            return false;
        }
    }

    /// <summary>
    /// JSON из формы — текстом, с отступом как в исходном файле (табы или N пробелов; по умолчанию 2 пробела),
    /// чтобы правка одного значения не перекраивала весь файл. Переводы строк — как в исходном.
    /// </summary>
    public static string Format(JToken root, string original)
    {
        var (ch, n) = (' ', 2);
        foreach (var line in original.Split('\n'))
        {
            var lead = line.Length - line.TrimStart(' ', '\t').Length;
            if (lead == 0 || line.Trim().Length == 0) continue;
            (ch, n) = line[0] == '\t' ? ('\t', 1) : (' ', Math.Min(lead, 8));
            break;
        }
        var sw = new StringWriter { NewLine = original.Contains("\r\n") ? "\r\n" : "\n" };
        using (var w = new JsonTextWriter(sw) { Formatting = Formatting.Indented, IndentChar = ch, Indentation = n })
            root.WriteTo(w);
        return sw.ToString();
    }

    /// <summary>
    /// Записать файл: JSON сначала проверяется (сломанный не записывается — <see cref="InvalidDataException"/>),
    /// прежняя версия уходит в резервные копии, запись — через временный файл рядом. BOM сохраняется, если был.
    /// </summary>
    public static void Save(ModConfigFile file, string text, ModConfigBackups backups)
    {
        if (file.Kind == ModConfigKind.Json && JsonError(text) is { } error) throw new InvalidDataException(error);
        var encoding = EncodingFor(file.Path, text); // в исходной кодировке и с той же меткой
        if (File.Exists(file.Path)) backups.Keep(file.Path, file.RelativePath);
        Platform.SafeFile.WriteAllText(file.Path, text, encoding); // папку (её ещё может не быть) создаст сам
    }

    /// <summary>
    /// Сбросить к стандартным: файл уходит в резервные копии и удаляется — мод при следующем запуске игры
    /// создаст его заново со значениями по умолчанию (так делает большинство модов). Отменяется «Вернуть как было».
    /// </summary>
    public static void Reset(ModConfigFile file, ModConfigBackups backups)
    {
        if (!File.Exists(file.Path)) return;
        backups.Keep(file.Path, file.RelativePath);
        File.Delete(file.Path);
    }

}

/// <summary>
/// Прежние версии конфигов: &lt;данные eViSTool&gt;\ModConfigBackups\&lt;профиль&gt;\&lt;путь в ModConfig&gt;\&lt;время&gt;.&lt;расширение&gt;.
/// Держим последние несколько версий файла. «Вернуть как было» — шаг назад: последняя версия встаёт на место
/// и уходит из истории; ещё раз — следующая.
/// </summary>
public sealed class ModConfigBackups(string root, int keep = 5)
{
    public string Root { get; } = root;

    public static ModConfigBackups ForProfile(GameProfile profile) => new(System.IO.Path.Combine(AppPaths.ModConfigBackups, profile.Id));

    private string DirFor(string relativePath) => System.IO.Path.Combine(Root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>Сохранить текущую версию файла.</summary>
    public void Keep(string path, string relativePath) =>
        Store(relativePath, System.IO.Path.GetExtension(path), target => File.Copy(path, target, overwrite: true));

    /// <summary>Сохранить версию, которой нет файлом на этой машине (скачанную с сервера у хостера).</summary>
    public void Keep(byte[] content, string relativePath) =>
        Store(relativePath, System.IO.Path.GetExtension(relativePath), target => File.WriteAllBytes(target, content));

    private const string NameFormat = "yyyyMMdd-HHmmss-fff";

    private void Store(string relativePath, string extension, Action<string> write)
    {
        var dir = Directory.CreateDirectory(DirFor(relativePath)).FullName;
        // две записи в одну миллисекунду (на быстром диске так и бывает) не должны затирать друг друга:
        // имя занято — следующая миллисекунда, порядок версий по имени сохраняется
        var at = DateTime.Now;
        // версии упорядочены по имени: осенью часы идут на час назад, и новая версия получила бы имя меньше прежней —
        // «Отменить» вернул бы не ту, а чистка удалила бы свежую. Поэтому имя всегда после самого нового
        if (Versions(relativePath).FirstOrDefault() is { } newest
            && DateTime.TryParseExact(System.IO.Path.GetFileNameWithoutExtension(newest), NameFormat,
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var last)
            && last >= at)
            at = last.AddMilliseconds(1);
        string target;
        while (File.Exists(target = System.IO.Path.Combine(dir, at.ToString(NameFormat, System.Globalization.CultureInfo.InvariantCulture) + extension)))
            at = at.AddMilliseconds(1);
        write(target);
        foreach (var old in Versions(relativePath).Skip(keep)) File.Delete(old);
    }

    /// <summary>Сохранённые версии файла, новые сверху.</summary>
    public IReadOnlyList<string> Versions(string relativePath)
    {
        var dir = DirFor(relativePath);
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).OrderByDescending(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal).ToList()
            : [];
    }

    /// <summary>
    /// Шаг назад: последняя сохранённая версия встаёт на место файла (через временный рядом) и уходит из истории.
    /// false — возвращать нечего.
    /// </summary>
    public bool Undo(string path, string relativePath)
    {
        if (Versions(relativePath).FirstOrDefault() is not { } last) return false;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tmp = path + ".evistool.tmp";
        File.Copy(last, tmp, overwrite: true);
        Platform.SafeFile.Replace(tmp, path);
        File.Delete(last);
        return true;
    }
}

using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using eViSTool.Core.Mods;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Diagnostics;

/// <summary>Установленный мод и то, по чему его узнать в логах: пространства имён и имена классов его сборок.</summary>
public sealed record ModPrint(string ModId, string Name, string? Version, string Path, IReadOnlySet<string> Namespaces,
    IReadOnlySet<string> TypeNames);

/// <summary>
/// «Отпечатки» установленных модов: по строке стека («CrashTestMod.Boom.Now») — чей код; по строке перевода — чей текст.
/// Пространства имён читаются из метаданных DLL внутри zip (без загрузки сборок). Код самой игры и .NET модам не
/// приписывается.
/// </summary>
public sealed class ModFingerprints
{
    private static readonly string[] NotMods = ["System.", "Microsoft.", "Vintagestory.", "OpenTK.", "Newtonsoft.", "HarmonyLib.", "Mono.", "ProtoBuf."];

    private readonly List<ModPrint> _mods;
    private Dictionary<string, ModPrint>? _translations;

    private ModFingerprints(List<ModPrint> mods) => _mods = mods;

    public IReadOnlyList<ModPrint> Mods => _mods;

    public static ModFingerprints Build(IEnumerable<LocalMod> mods) =>
        new([.. mods.Where(m => m.Info is not null)
            .Select(m =>
            {
                var (namespaces, types) = ReadCode(m.Path);
                return new ModPrint(m.Info!.ModId, m.Info.Name ?? m.Info.ModId, m.Info.Version, m.Path, namespaces, types);
            })]);

    /// <summary>
    /// Отпечатки модов, файлов которых на этой машине нет (сервер у хостера: пути — на FTP): только modid, имя и версия.
    /// Виновника тогда называют слово игры и метки в сообщениях, по стеку и строкам перевода — нет (код не прочитан).
    /// Диск этой машины по таким путям не читается.
    /// </summary>
    public static ModFingerprints WithoutCode(IEnumerable<LocalMod> mods) =>
        new([.. mods.Where(m => m.Info is not null)
            .Select(m => new ModPrint(m.Info!.ModId, m.Info.Name ?? m.Info.ModId, m.Info.Version, m.Path,
                new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal)))])
        {
            _translations = new Dictionary<string, ModPrint>(StringComparer.Ordinal),
        };

    public ModPrint? ByModId(string modId) =>
        _mods.FirstOrDefault(m => string.Equals(m.ModId, modId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Чей код в этой строке стека: самое длинное совпавшее пространство имён. null — игра, .NET или не узнать.</summary>
    public ModPrint? ByFrame(string frame)
    {
        if (NotMods.Any(p => frame.StartsWith(p, StringComparison.Ordinal))) return null;
        ModPrint? best = null;
        var bestLength = 0;
        foreach (var mod in _mods)
            foreach (var ns in mod.Namespaces)
                if (ns.Length > bestLength && frame.StartsWith(ns + ".", StringComparison.Ordinal))
                    (best, bestLength) = (mod, ns.Length);
        return best ?? ByNamespaceName(frame);
    }

    /// <summary>
    /// Код мода не прочитан (у хостера файлы на FTP, DLL не качаем): угадать по первому слову пространства имён — моды
    /// обычно называют его как modid или имя («EvistoolCrashTest.Boom» → evistoolcrashtest). Только для модов без
    /// прочитанного кода и при полном совпадении, чтобы не приписать вылет чужому.
    /// </summary>
    private ModPrint? ByNamespaceName(string frame)
    {
        var first = frame.Split('.', 2)[0];
        var key = Squash(first);
        if (key.Length < 4 || first.Length == frame.Length) return null;
        return _mods.FirstOrDefault(m => m.Namespaces.Count == 0 && (Squash(m.ModId) == key || Squash(m.Name) == key));
    }

    private static string Squash(string s) => new([.. s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    /// <summary>
    /// Мод по метке в сообщении: «[carryon]», «[Carry On]», «[BlockBehaviorAutoStashable]», «for mod Unforgettable.Core» —
    /// modid, имя мода, имя его класса или его пространство имён.
    /// </summary>
    public ModPrint? ByTag(string tag)
    {
        var t = tag.Trim();
        if (t.Length == 0) return null;
        return _mods.FirstOrDefault(m => string.Equals(m.ModId, t, StringComparison.OrdinalIgnoreCase))
               ?? _mods.FirstOrDefault(m => Squash(m.Name) == Squash(t) && Squash(t).Length > 2)
               // пространство имён или имя сборки («Unforgettable.Core» при пространстве «Unforgettable»)
               ?? _mods.FirstOrDefault(m => m.Namespaces.Any(ns => ns == t || t.StartsWith(ns + ".", StringComparison.Ordinal)
                                                                    || ns.StartsWith(t + ".", StringComparison.Ordinal)))
               ?? _mods.FirstOrDefault(m => m.TypeNames.Contains(t));
    }

    /// <summary>Первый мод в стеке (сверху — ближе к месту ошибки).</summary>
    public ModPrint? ByStack(IEnumerable<string> frames) => frames.Select(ByFrame).FirstOrDefault(m => m is not null);

    /// <summary>Чья это строка перевода (из assets/*/lang/*.json модов). null — не нашли.</summary>
    public ModPrint? ByTranslation(string text)
    {
        _translations ??= ReadTranslations();
        return _translations.GetValueOrDefault(text);
    }

    private Dictionary<string, ModPrint> ReadTranslations()
    {
        var map = new Dictionary<string, ModPrint>(StringComparer.Ordinal);
        foreach (var mod in _mods)
            foreach (var (name, read) in Files(mod.Path))
            {
                if (!name.Contains("/lang/", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var reader = new StreamReader(read());
                    if (JToken.Parse(reader.ReadToEnd()) is not JObject lang) continue;
                    foreach (var p in lang.Properties())
                        if (p.Value.Type == JTokenType.String) map.TryAdd(p.Value.ToString(), mod);
                }
                catch (Exception ex) when (ex is IOException or Newtonsoft.Json.JsonException or InvalidDataException) { }
            }
        return map;
    }

    private static (HashSet<string> Namespaces, HashSet<string> Types) ReadCode(string path)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, read) in Files(path))
        {
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var ms = new MemoryStream();
                using (var s = read()) s.CopyTo(ms);
                ms.Position = 0;
                using var pe = new PEReader(ms);
                if (!pe.HasMetadata) continue;
                var md = pe.GetMetadataReader();
                foreach (var handle in md.TypeDefinitions)
                {
                    var type = md.GetTypeDefinition(handle);
                    var ns = md.GetString(type.Namespace);
                    if (ns.Length > 0) result.Add(ns);
                    var typeName = md.GetString(type.Name);
                    // свои классы мода; служебные компилятора («<>c») и «Module» не в счёт
                    if (ns.Length > 0 && typeName.Length > 3 && !typeName.StartsWith('<')) types.Add(typeName.Split('`')[0]);
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException or InvalidDataException) { }
        }
        // чужие пространства имён (мод положил типы в Vintagestory.GameContent) не считаем: иначе мод «виноват» в коде игры
        result.RemoveWhere(ns => NotMods.Any(p => (ns + ".").StartsWith(p, StringComparison.Ordinal)));
        return (result, types);
    }

    /// <summary>Файлы мода: из zip или из папки (распакованный мод). Имя — с прямыми слэшами.</summary>
    private static IEnumerable<(string Name, Func<Stream> Open)> Files(string path)
    {
        if (Directory.Exists(path))
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                yield return (System.IO.Path.GetRelativePath(path, f).Replace('\\', '/'), () => File.OpenRead(f));
            yield break;
        }
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { yield break; }
        using (zip)
            foreach (var e in zip.Entries.Where(e => e.Name.Length > 0))
                yield return (e.FullName, e.Open);
    }
}

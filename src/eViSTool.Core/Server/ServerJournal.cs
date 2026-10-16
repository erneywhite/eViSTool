using eViSTool.Core.Platform;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Server;

/// <summary>
/// Запись журнала действий с сервером. At — в UTC (окно показывает по своим часам, пояс сервера не важен). Action — код действия (подпись в окне — <c>journal.act.&lt;код&gt;</c> на языке
/// окна), Args — подробности (команда, файл мода, имя копии). Who — имя компьютера окна (его сообщает само окно, подделать
/// можно, но «кто из своих» видно), Ip — откуда пришёл запрос; у окна на машине с сервером Remote = false.
/// </summary>
public sealed record JournalEntry(DateTime At, string Action, IReadOnlyList<string> Args, string? Who, string? Ip, bool Remote);

/// <summary>
/// Журнал действий с сервером через агента: кто, когда и что поменял (запуск, команды, моды, конфиг, копии) и неудачные
/// попытки подключиться. Пишет только агент — по строке JSON на действие; читают агент (для удалённого окна) и окно на
/// этой машине. Хранится <see cref="Keep"/>; чтение запросов (статус, консоль) не пишется — окно опрашивает их постоянно.
/// </summary>
public static class ServerJournal
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    /// <summary>Заголовок, в котором окно сообщает имя своего компьютера (в URL-кодировке: имя бывает не латиницей).</summary>
    public const string ClientHeader = "X-eViSTool-Client";

    public static string FileFor(string profileId, string? agentsDir = null) =>
        Path.Combine(agentsDir ?? AgentProtocol.DefaultAgentsDir, $"{profileId}.journal.jsonl");

    private static readonly Lock Gate = new();

    public static void Append(string file, JournalEntry entry)
    {
        var line = JsonConvert.SerializeObject(entry, Formatting.None) + "\n";
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, line);
        }
    }

    /// <summary>Записи за <see cref="Keep"/>, новые сверху. Битые строки (оборванная запись) пропускаются.</summary>
    public static IReadOnlyList<JournalEntry> Read(string file, DateTime utcNow)
    {
        var from = utcNow - Keep;
        // при равном времени новее та, что ниже в файле
        return [.. Lines(file).Select((e, i) => (e, i)).Where(x => x.e.At >= from)
            .OrderByDescending(x => x.e.At).ThenByDescending(x => x.i).Select(x => x.e)];
    }

    /// <summary>Убрать записи старше <see cref="Keep"/> (агент — при запуске и раз в сутки).</summary>
    public static void Prune(string file, DateTime utcNow)
    {
        lock (Gate)
        {
            if (!File.Exists(file)) return;
            var from = utcNow - Keep;
            var all = Lines(file).ToList();
            var keep = all.Where(e => e.At >= from).ToList();
            if (keep.Count == all.Count) return;
            var tmp = file + ".tmp";
            File.WriteAllLines(tmp, keep.Select(e => JsonConvert.SerializeObject(e, Formatting.None)));
            SafeFile.Replace(tmp, file);
        }
    }

    private static IEnumerable<JournalEntry> Lines(string file)
    {
        if (!File.Exists(file)) yield break;
        List<string> lines = [];
        // агент может дописывать в этот момент — открываем, не мешая ему
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
            while (reader.ReadLine() is { } line) lines.Add(line);
        foreach (var line in lines)
        {
            JournalEntry? entry = null;
            try { entry = JsonConvert.DeserializeObject<JournalEntry>(line); }
            catch (JsonException) { }
            if (entry is { Action.Length: > 0 }) yield return entry with { Args = entry.Args ?? [] };
        }
    }

    /// <summary>
    /// Какое действие означает запрос к агенту: код и подробности. null — запрос ничего не меняет (статус, консоль, чтение
    /// списков) и в журнал не идёт. body — тело запроса (JSON), fileHeader — имя присланного архива мода.
    /// </summary>
    public static (string Action, string[] Args)? Describe(string method, string path, string? body, string? fileHeader)
    {
        JObject? json = null;
        if (!string.IsNullOrEmpty(body))
            try { json = JObject.Parse(body); } catch (JsonException) { }
        string Str(string name) => json?[name]?.ToString() ?? "";
        string FileName(string name) => Path.GetFileName(Str(name).Replace('\\', '/'));
        bool On(string name) => json?[name]?.Type == JTokenType.Boolean && json[name]!.Value<bool>();

        return (method.ToUpperInvariant(), path.TrimEnd('/').ToLowerInvariant()) switch
        {
            ("POST", "/start") => ("start", []),
            ("POST", "/stop") => ("stop", []),
            ("POST", "/restart") => ("restart", []),
            ("POST", "/kill") => ("kill", []),
            ("POST", "/command") => ("command", [Str("Text").Trim()]),
            ("POST", "/backup") or ("POST", "/backups/copy") => ("backup", []),
            ("POST", "/backups/restore") => ("restore", [Str("Name")]),
            ("POST", "/backups/delete") => ("backupDelete", [Str("Name")]),
            ("PUT", "/automation") => ("schedule", []),
            ("PUT", "/notify") => ("notify", []),
            ("PUT", "/announcements") => ("announcements", []),
            ("POST", "/mods/install") => ("modInstall", [Path.GetFileName(Uri.UnescapeDataString(fileHeader ?? ""))]),
            ("POST", "/mods/toggle") => (On("Enabled") ? "modOn" : "modOff", [FileName("Path")]),
            ("POST", "/mods/delete") => ("modDelete", [FileName("Path")]),
            ("POST", "/players/edit") => ("players", []),
            ("POST", "/modconfig/save") => ("modConfigSave", [Str("Path")]),
            ("POST", "/modconfig/undo") => ("modConfigUndo", [Str("Path")]),
            ("POST", "/modconfig/reset") => ("modConfigReset", [Str("Path")]),
            ("PUT", "/config") => ("config", []),
            ("POST", "/config/generate") => ("configGenerate", []),
            ("POST", "/stats/enabled") => (On("Enabled") ? "statsOn" : "statsOff", []),
            ("POST", "/stats/clear") => ("statsClear", []),
            ("POST", "/self-update") => ("selfUpdate", [Str("Version")]),
            _ => null,
        };
    }

    /// <summary>Нужно ли этому запросу тело для <see cref="Describe"/> (архив мода не читаем — он большой и не нужен).</summary>
    public static bool NeedsBody(string path) => path.TrimEnd('/').ToLowerInvariant() is
        "/command" or "/backups/restore" or "/backups/delete" or "/mods/toggle" or "/mods/delete"
        or "/modconfig/save" or "/modconfig/undo" or "/modconfig/reset" or "/stats/enabled" or "/self-update";
}

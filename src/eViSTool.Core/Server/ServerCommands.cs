using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace eViSTool.Core.Server;

/// <summary>Команда сервера из его /help: имя, аргументы («&lt;source&gt; &lt;target&gt;») и что делает.</summary>
public sealed record ServerCommand(string Name, string Args, string Description)
{
    /// <summary>«/tp &lt;source&gt; &lt;target&gt;».</summary>
    [JsonIgnore]
    public string Usage => "/" + Name + (Args.Length > 0 ? " " + Args : "");
}

/// <summary>
/// Список команд сервера — берётся у него самого из ответа на /help (вместе с командами модов), не из зашитого списка.
/// Строка ответа: «&lt;code&gt;/tp &lt;i&gt;&amp;lt;source&amp;gt;&lt;/i&gt; &lt;/code&gt; :  Teleport…».
/// </summary>
public static partial class ServerCommands
{
    [GeneratedRegex(@"^<code>/(?<name>[^\s<]+)(?<args>.*?)</code>\s*:\s*(?<desc>.*)$", RegexOptions.Singleline)]
    private static partial Regex HelpLine();

    /// <summary>Строка ответа на /help → команда (false — это не строка списка команд).</summary>
    public static bool TryParseHelpLine(string raw, out ServerCommand command)
    {
        command = null!;
        var m = HelpLine().Match(raw.Trim());
        if (!m.Success) return false;
        var args = Collapse(ConsoleMarkup.ToPlain(m.Groups["args"].Value));
        // описание бывает в несколько строк («…tools.<br> If you want…») — в подсказку идёт первая
        var desc = ConsoleMarkup.ToPlain(m.Groups["desc"].Value).Split('\n')[0].Trim();
        command = new ServerCommand(m.Groups["name"].Value, args, desc);
        return true;
    }

    private static string Collapse(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Подсказки к набранному: «/te» → команды на «te» (сначала совпавшие с начала, потом по алфавиту).
    /// Набрано имя и пробел — подсказывать уже нечего (дальше аргументы).
    /// </summary>
    public static IReadOnlyList<ServerCommand> Suggest(IEnumerable<ServerCommand> all, string input, int max = 8)
    {
        if (!input.StartsWith('/') || input.Contains(' ')) return [];
        var typed = input[1..];
        return all.Where(c => c.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name.Length != typed.Length) // точное совпадение — первым
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    /// <summary>Команда, которую набирают («/tp Erney …» → /tp) — для подсказки аргументов.</summary>
    public static ServerCommand? Typed(IEnumerable<ServerCommand> all, string input)
    {
        if (!input.StartsWith('/')) return null;
        var name = input[1..].Split(' ', 2)[0];
        return all.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Что дописать по Tab: общее начало всех подходящих имён («/wor» → «/worldconfig»); null — дописать нечего.</summary>
    public static string? Complete(IReadOnlyList<ServerCommand> suggestions, string input)
    {
        if (suggestions.Count == 0) return null;
        if (suggestions.Count == 1) return "/" + suggestions[0].Name + " ";
        var prefix = suggestions[0].Name;
        foreach (var c in suggestions.Skip(1))
        {
            var n = 0;
            while (n < prefix.Length && n < c.Name.Length && char.ToLowerInvariant(prefix[n]) == char.ToLowerInvariant(c.Name[n])) n++;
            prefix = prefix[..n];
        }
        var completed = "/" + prefix;
        return completed.Length > input.Length ? completed : null;
    }

    // ---- хранение: список запоминается для профиля, чтобы подсказки были и до первого /help в этом сеансе

    public static string FileFor(string profileId, string? agentsDir = null) =>
        Path.Combine(agentsDir ?? AgentProtocol.DefaultAgentsDir, $"{profileId}.commands.json");

    public static List<ServerCommand> Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonConvert.DeserializeObject<List<ServerCommand>>(File.ReadAllText(file)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void Save(string file, IEnumerable<ServerCommand> commands)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(commands.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase), Formatting.Indented));
        File.Move(tmp, file, overwrite: true);
    }
}

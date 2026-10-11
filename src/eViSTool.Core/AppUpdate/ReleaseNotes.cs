using System.Text.RegularExpressions;

namespace eViSTool.Core.AppUpdate;

/// <summary>Раздел списка изменений: заголовок («Новое», «Исправления»; null — без заголовка) и пункты.</summary>
public sealed record ReleaseNotesSection(string? Heading, IReadOnlyList<string> Items);

/// <summary>
/// Список изменений релиза для окна «Вышла новая версия», разобранный из текста релиза на GitHub. Текст пишется по
/// одному образцу: английская часть, строка «---», русская. В каждой — строка-итог («**eViSTool 0.10.3: …**» или
/// просто фраза), разделы с жирным заголовком и пункты «- …», в конце — «Обнови из „О программе“…» и ссылки на ModDB.
/// </summary>
public sealed partial record ReleaseNotes(string? Summary, IReadOnlyList<ReleaseNotesSection> Sections)
{
    public bool IsEmpty => Summary is null && Sections.Count == 0;

    /// <summary>Есть ли в тексте релиза пометка «&lt;!-- evistool: recommended --&gt;» (в любой части, любым регистром).</summary>
    public static bool IsRecommended(string? body) => body is not null && RecommendedMark().IsMatch(body);

    /// <param name="russian">Взять русскую часть (вторую после «---»), если она есть; иначе — английскую.</param>
    public static ReleaseNotes Parse(string? body, bool russian)
    {
        var lines = (body ?? "").Replace("\r\n", "\n").Split('\n');
        var parts = new List<List<string>> { new() };
        foreach (var line in lines)
        {
            if (line.Trim() == "---") parts.Add([]);
            else parts[^1].Add(line);
        }
        var part = russian && parts.Count > 1 ? parts[1] : parts[0];

        string? summary = null;
        var sections = new List<ReleaseNotesSection>();
        string? heading = null;
        List<string>? items = null;
        foreach (var raw in part)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("<!--")) continue; // служебные пометки (recommended) — не для чтения
            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                if (items is null) sections.Add(new ReleaseNotesSection(heading, items = []));
                items.Add(Plain(line[2..]));
                continue;
            }
            if (Bold().Match(line) is { Success: true } bold && (summary is not null || sections.Count > 0))
            {
                heading = Plain(bold.Groups[1].Value).TrimEnd(':');
                items = null; // следующий пункт начнёт новый раздел под этим заголовком
                continue;
            }
            // строка-итог — до первого пункта; текст после пунктов («Обнови из…», ссылки на ModDB) окну не нужен
            if (summary is null && sections.Count == 0) summary = WithoutProduct(Plain(line));
        }
        return new ReleaseNotes(summary, sections);
    }

    /// <summary>«eViSTool 0.10.3: reliability fixes.» → «Reliability fixes»: версия и так видна рядом.</summary>
    private static string WithoutProduct(string line)
    {
        var text = ProductPrefix().Replace(line, "").TrimEnd('.');
        return text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : line;
    }

    /// <summary>Markdown → простой текст: «**жирное**» и «`код`» без знаков, «[текст](ссылка)» → «текст».</summary>
    private static string Plain(string text) =>
        Link().Replace(text, "$1").Replace("**", "").Replace("`", "").Trim();

    [GeneratedRegex(@"^\*\*(.+)\*\*$")]
    private static partial Regex Bold();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^eViSTool\s+\S+:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ProductPrefix();

    [GeneratedRegex(@"<!--\s*evistool\s*:\s*recommended\s*-->", RegexOptions.IgnoreCase)]
    private static partial Regex RecommendedMark();
}

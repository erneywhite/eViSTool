using System.Net;
using System.Text.RegularExpressions;

namespace eViSTool.App;

/// <summary>Описания и changelog из модбазы приходят в HTML — показываем простым текстом с абзацами.</summary>
internal static partial class Html
{
    public static string? ToPlainText(string? html, int maxLength = 0)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = Breaks().Replace(html, "\n");
        text = ListItems().Replace(text, "\n• ");
        text = Tags().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = ManyNewlines().Replace(text, "\n\n").Trim();
        return maxLength > 0 && text.Length > maxLength ? text[..maxLength] + "…" : text;
    }

    [GeneratedRegex(@"<\s*(br|/p|/div|/h\d|/li|/ul|/ol)\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex Breaks();

    [GeneratedRegex(@"<\s*li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItems();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\n\s*\n(\s*\n)+")]
    private static partial Regex ManyNewlines();
}

using System.Net;
using System.Text.RegularExpressions;

namespace eViSTool.Core.Server;

/// <summary>
/// Ответы сервера бывают с разметкой игры (VTML): /help присылает &lt;code&gt;, &lt;i&gt;, ссылки и коды вроде &amp;lt;.
/// Для консоли — обычный текст. Снимаются только теги разметки игры: «List&lt;string&gt;» в стеке ошибки остаётся как есть.
/// </summary>
public static partial class ConsoleMarkup
{
    [GeneratedRegex(@"<br\s*/?>[ \t]*", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"</?(?:code|i|b|u|em|strong|a|font|hk|hotkey|icon|itemstack|clear|strike|p|div|span)(?:\s[^<>]*)?/?>", RegexOptions.IgnoreCase)]
    private static partial Regex Tag();

    public static string ToPlain(string text)
    {
        if (text.IndexOf('<') < 0 && text.IndexOf('&') < 0) return text;
        var plain = Tag().Replace(LineBreak().Replace(text, "\n"), "");
        return WebUtility.HtmlDecode(plain);
    }
}

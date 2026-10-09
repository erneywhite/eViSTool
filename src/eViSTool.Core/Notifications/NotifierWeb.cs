using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using eViSTool.Core.Localization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Notifications;

/// <summary>Готовые шаблоны тела вебхука: выбрал — и вставил адрес.</summary>
public static class WebhookTemplates
{
    public const string Json = """
        {
          "severity": "{severity}",
          "server": "{server}",
          "title": "{title}",
          "details": "{details}",
          "text": "{text}",
          "time": "{time}"
        }
        """;

    public const string Slack = """{ "text": "{text}" }""";

    public const string HomeAssistant = """{ "title": "{server} · {title}", "message": "{details}", "severity": "{severity}" }""";
}

/// <summary>Discord, ntfy и свой вебхук — та же <see cref="Notifier"/>, по HTTP-запросу на адрес.</summary>
public sealed partial class Notifier
{
    public const string DefaultNtfyServer = "https://ntfy.sh";

    /// <summary>Цвет полосы Discord — те же, что у кружков.</summary>
    private static int Color(NotifySeverity s) => s switch
    {
        NotifySeverity.Problem => 0xE24B4A,
        NotifySeverity.Warning => 0xEF9F27,
        NotifySeverity.Info => 0x378ADD,
        NotifySeverity.Good => 0x639922,
        _ => 0x888780,
    };

    // ---------- Discord: карточка (embed) с цветной полосой

    [GeneratedRegex(@"^https://(?:(?:ptb|canary)\.)?discord(?:app)?\.com/api/webhooks/\d+/[\w-]+/?$")]
    private static partial Regex DiscordUrl();

    public static bool IsDiscordUrl(string url) => DiscordUrl().IsMatch(url.Trim());

    /// <summary>Подробности для Discord: «⚑ *Мод:* текст», спецсимволы Markdown в тексте экранированы.</summary>
    public static string DiscordDescription(NotifyMessage m) => string.Join("\n", m.Details.Select(d => string.Join(" ", new[]
    {
        d.Icon, d.Label is { Length: > 0 } label ? $"*{DiscordEscape(label)}:*" : "", DiscordEscape(d.Text),
    }.Where(s => s.Length > 0))));

    private static string DiscordEscape(string s) => Regex.Replace(s, @"([\\*_~`|>#\[\]()-])", @"\$1");

    private async Task DiscordAsync(string url, NotifyMessage m, CancellationToken ct)
    {
        if (!IsDiscordUrl(url)) throw new NotifyException(Loc.T("notify.dcBadUrl"));
        var embed = new JObject { ["title"] = m.Title, ["color"] = Color(m.Severity) };
        if (!string.IsNullOrWhiteSpace(m.Server)) embed["author"] = new JObject { ["name"] = m.Server };
        if (m.Details.Count > 0) embed["description"] = DiscordDescription(m);
        var body = new JObject { ["username"] = "eViSTool", ["embeds"] = new JArray(embed) };
        await PostAsync(url, body.ToString(Formatting.None), "discord.com", (code, _) => code switch
        {
            401 or 404 => Loc.T("notify.dcGone"),
            429 => Loc.T("notify.tooOften"),
            _ => null,
        }, ct).ConfigureAwait(false);
    }

    // ---------- ntfy: push на телефон, приоритет и значок — по цвету

    /// <summary>Длинная случайная тема: тема в ntfy — как пароль, кто её знает, тот читает.</summary>
    public static string NewNtfyTopic() => "evistool-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    public static bool IsNtfyTopic(string topic) => Regex.IsMatch(topic, @"^[\w-]{1,64}$");

    private async Task NtfyAsync(string? server, string topic, NotifyMessage m, CancellationToken ct)
    {
        topic = topic.Trim();
        if (!IsNtfyTopic(topic)) throw new NotifyException(Loc.T("notify.ntfyBadTopic"));
        var root = string.IsNullOrWhiteSpace(server) ? DefaultNtfyServer : server.Trim().TrimEnd('/');
        if (!Uri.TryCreate(root, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new NotifyException(Loc.T("notify.ntfyBadServer"));
        var body = new JObject
        {
            ["topic"] = topic,
            ["title"] = string.IsNullOrWhiteSpace(m.Server) ? m.Title : $"{m.Server} · {m.Title}",
            ["message"] = m.Details.Count > 0 ? string.Join("\n", m.Details.Select(PlainLine)) : m.Title,
            // проблемы — высокий приоритет (телефон зазвенит), спокойное — низкий
            ["priority"] = m.Severity switch { NotifySeverity.Problem => 4, NotifySeverity.Neutral => 2, _ => 3 },
            ["tags"] = new JArray(m.Severity switch
            {
                NotifySeverity.Problem => "red_circle",
                NotifySeverity.Warning => "yellow_circle",
                NotifySeverity.Info => "blue_circle",
                NotifySeverity.Good => "green_circle",
                _ => "white_circle",
            }),
        };
        await PostAsync(root, body.ToString(Formatting.None), uri.Host, (code, _) => code switch
        {
            401 or 403 => Loc.T("notify.ntfyForbidden"),
            429 => Loc.T("notify.tooOften"),
            _ => null,
        }, ct).ConfigureAwait(false);
    }

    private static string PlainLine(NotifyLine d) => string.Join(" ", new[]
    {
        d.Icon, d.Label is { Length: > 0 } l ? l + ":" : "", d.Text,
    }.Where(s => s.Length > 0));

    // ---------- свой вебхук: шаблон тела с подстановками

    /// <summary>Тело запроса из шаблона: значения подставляются экранированными для строки JSON. Не JSON — ошибка.</summary>
    public static string WebhookBody(string? template, NotifyMessage m, DateTime now)
    {
        var values = new Dictionary<string, string>
        {
            ["severity"] = m.Severity.ToString().ToLowerInvariant(),
            ["color"] = "#" + Color(m.Severity).ToString("X6", CultureInfo.InvariantCulture),
            ["server"] = m.Server ?? "",
            ["title"] = m.Title,
            ["details"] = string.Join("\n", m.Details.Select(PlainLine)),
            ["text"] = m.ToPlainText(),
            ["time"] = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        };
        var body = Regex.Replace(string.IsNullOrWhiteSpace(template) ? WebhookTemplates.Json : template, @"\{(\w+)\}",
            x => values.TryGetValue(x.Groups[1].Value, out var v) ? JsonConvert.ToString(v)[1..^1] : x.Value);
        try { JToken.Parse(body); }
        catch (JsonException) { throw new NotifyException(Loc.T("notify.whBadTemplate")); }
        return body;
    }

    private Task WebhookAsync(string url, string? template, NotifyMessage m, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new NotifyException(Loc.T("notify.whBadUrl"));
        return PostAsync(url.Trim(), WebhookBody(template, m, DateTime.Now), uri.Host, (_, _) => null, ct);
    }

    /// <summary>
    /// POST JSON. Ошибку описываем своими словами и только по имени сервера: в адресе (а значит, и в тексте исключений)
    /// может быть секрет — у вебхуков он прямо в ссылке.
    /// </summary>
    private async Task PostAsync(string url, string json, string host, Func<int, string, string?> explain, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            resp = await http.PostAsync(url, content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            throw new NotifyException(Loc.T("notify.network", host));
        }
        using (resp)
        {
            if (resp.IsSuccessStatusCode) return;
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new NotifyException(explain((int)resp.StatusCode, text) ?? Loc.T("notify.httpError", host, (int)resp.StatusCode));
        }
    }
}

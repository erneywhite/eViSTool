using System.Net.Http;
using System.Text;
using eViSTool.Core.Localization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Notifications;

/// <summary>Чат Telegram, который нашёлся по последним сообщениям боту: личка, группа, канал или тема форума.</summary>
public sealed record TelegramChat(string Id, string Title, long? TopicId = null);

/// <summary>
/// Отправка оповещений. Ошибки — <see cref="NotifyException"/> с понятной причиной; секрет (токен, адрес вебхука)
/// в тексте ошибки не появляется никогда.
/// </summary>
public sealed partial class Notifier(HttpClient http)
{
    private const string TelegramApi = "https://api.telegram.org/bot";

    public async Task SendAsync(NotifyChannel channel, NotifyMessage message, CancellationToken ct = default)
    {
        var secret = channel.Secret ?? throw new NotifyException(Loc.T("notify.noSecret"));
        switch (channel.Kind)
        {
            case NotifyKind.Telegram:
                if (string.IsNullOrWhiteSpace(channel.ChatId)) throw new NotifyException(Loc.T("notify.tgNoChat"));
                // разметка HTML (а не Markdown): в именах модов и игроков бывают * и _, а в HTML экранировать надо только < > &
                var body = new JObject
                {
                    ["chat_id"] = channel.ChatId,
                    ["text"] = TelegramHtml(message),
                    ["parse_mode"] = "HTML",
                    ["disable_web_page_preview"] = true,
                };
                if (channel.TopicId is { } topic) body["message_thread_id"] = topic; // тема форума
                await TelegramAsync(secret, "sendMessage", body, ct).ConfigureAwait(false);
                break;
            case NotifyKind.Discord:
                await DiscordAsync(secret, message, ct).ConfigureAwait(false);
                break;
            case NotifyKind.Ntfy:
                await NtfyAsync(channel.Url, secret, message, ct).ConfigureAwait(false);
                break;
            case NotifyKind.Webhook:
                await WebhookAsync(secret, channel.Template, message, ct).ConfigureAwait(false);
                break;
            default:
                throw new NotifyException(Loc.T("notify.kindLater"));
        }
    }

    /// <summary>«🔴 <b>Survival</b>» / «<b>Сервер упал</b>» / «⚑ <i>Мод:</i> …»; без сервера — кружок у события.</summary>
    public static string TelegramHtml(NotifyMessage m)
    {
        static string E(string s) => System.Net.WebUtility.HtmlEncode(s);
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(m.Server)) lines.Add($"{m.Dot} <b>{E(m.Title)}</b>");
        else
        {
            lines.Add($"{m.Dot} <b>{E(m.Server)}</b>");
            lines.Add($"<b>{E(m.Title)}</b>");
        }
        lines.AddRange(m.Details.Select(d => string.Join(" ", new[]
        {
            d.Icon, d.Label is { Length: > 0 } label ? $"<i>{E(label)}:</i>" : "", E(d.Text),
        }.Where(s => s.Length > 0))));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Telegram: в каких чатах боту писали в последнее время (новые — первыми): личка, группы, каналы, а в форумах —
    /// каждая тема отдельно. Человек пишет боту «привет» (или в нужной теме) — и чат находится сам, без поиска его ID.
    /// </summary>
    public async Task<IReadOnlyList<TelegramChat>> FindTelegramChatsAsync(string token, CancellationToken ct = default)
    {
        var result = await TelegramAsync(token, "getUpdates", new JObject { ["allowed_updates"] = new JArray("message", "channel_post", "my_chat_member") }, ct)
            .ConfigureAwait(false);
        var chats = new List<TelegramChat>();
        foreach (var update in (result as JArray ?? []).Reverse())
        {
            var message = update["message"] ?? update["channel_post"] ?? update["my_chat_member"];
            var chat = message?["chat"];
            if (chat?["id"] is not { } id) continue;
            // сообщение в теме форума: номер темы, а её имя — в служебном сообщении о создании темы, на которое оно «отвечает»
            long? topic = message!.Value<bool?>("is_topic_message") == true ? message.Value<long?>("message_thread_id") : null;
            if (chats.Any(c => c.Id == id.ToString() && c.TopicId == topic)) continue;
            var title = chat.Value<string>("title")
                        ?? (chat.Value<string>("username") is { } user ? "@" + user : null)
                        ?? string.Join(" ", new[] { chat.Value<string>("first_name"), chat.Value<string>("last_name") }.Where(s => !string.IsNullOrEmpty(s)));
            if (title.Length == 0) title = id.ToString();
            if (topic is { } t)
                title += " › " + (message["reply_to_message"]?["forum_topic_created"]?.Value<string>("name") ?? "#" + t);
            chats.Add(new TelegramChat(id.ToString(), title, topic));
        }
        return chats;
    }

    private async Task<JToken?> TelegramAsync(string token, string method, JObject body, CancellationToken ct)
    {
        token = token.Trim();
        if (token.Length < 20 || token.Any(char.IsWhiteSpace) || !token.Contains(':'))
            throw new NotifyException(Loc.T("notify.tgBadToken"));
        HttpResponseMessage resp;
        try
        {
            using var content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            resp = await http.PostAsync(TelegramApi + token + "/" + method, content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // в тексте исключения может оказаться адрес запроса — а в нём токен; причину пересказываем своими словами
            throw new NotifyException(Loc.T("notify.network", "api.telegram.org"));
        }
        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JObject? json = null;
            try { json = JObject.Parse(text); } catch (JsonException) { }
            if (json?.Value<bool>("ok") == true) return json["result"];
            var description = json?.Value<string>("description") ?? $"HTTP {(int)resp.StatusCode}";
            throw new NotifyException((int)resp.StatusCode switch
            {
                401 or 404 => Loc.T("notify.tgUnauthorized"),
                403 => Loc.T("notify.tgBlocked"),
                409 => Loc.T("notify.tgWebhook"),
                _ => Loc.T("notify.tgError", description.Replace(token, "…", StringComparison.Ordinal)),
            });
        }
    }
}

public sealed class NotifyException(string message) : Exception(message);

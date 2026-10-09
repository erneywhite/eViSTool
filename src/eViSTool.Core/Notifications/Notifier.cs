using System.Net.Http;
using System.Text;
using eViSTool.Core.Localization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Notifications;

/// <summary>Чат Telegram, который нашёлся по последним сообщениям боту.</summary>
public sealed record TelegramChat(string Id, string Title);

/// <summary>
/// Отправка оповещений. Ошибки — <see cref="NotifyException"/> с понятной причиной; секрет (токен, адрес вебхука)
/// в тексте ошибки не появляется никогда.
/// </summary>
public sealed class Notifier(HttpClient http)
{
    private const string TelegramApi = "https://api.telegram.org/bot";

    public async Task SendAsync(NotifyChannel channel, NotifyMessage message, CancellationToken ct = default)
    {
        var secret = channel.Secret ?? throw new NotifyException(Loc.T("notify.noSecret"));
        switch (channel.Kind)
        {
            case NotifyKind.Telegram:
                if (string.IsNullOrWhiteSpace(channel.ChatId)) throw new NotifyException(Loc.T("notify.tgNoChat"));
                // без разметки: в именах модов и игроков бывают * и _, которые Markdown понял бы по-своему
                await TelegramAsync(secret, "sendMessage", new JObject
                {
                    ["chat_id"] = channel.ChatId,
                    ["text"] = message.Title + "\n" + message.Text,
                    ["disable_web_page_preview"] = true,
                }, ct).ConfigureAwait(false);
                break;
            default:
                throw new NotifyException(Loc.T("notify.kindLater"));
        }
    }

    /// <summary>
    /// Telegram: в каких чатах боту писали в последнее время (новые — первыми). Человек пишет боту «привет» — и чат
    /// находится сам, без поиска его ID.
    /// </summary>
    public async Task<IReadOnlyList<TelegramChat>> FindTelegramChatsAsync(string token, CancellationToken ct = default)
    {
        var result = await TelegramAsync(token, "getUpdates", new JObject { ["allowed_updates"] = new JArray("message", "channel_post", "my_chat_member") }, ct)
            .ConfigureAwait(false);
        var chats = new List<TelegramChat>();
        foreach (var update in (result as JArray ?? []).Reverse())
        {
            var chat = (update["message"] ?? update["channel_post"] ?? update["my_chat_member"])?["chat"];
            if (chat?["id"] is not { } id || chats.Any(c => c.Id == id.ToString())) continue;
            var title = chat.Value<string>("title")
                        ?? (chat.Value<string>("username") is { } user ? "@" + user : null)
                        ?? string.Join(" ", new[] { chat.Value<string>("first_name"), chat.Value<string>("last_name") }.Where(s => !string.IsNullOrEmpty(s)));
            chats.Add(new TelegramChat(id.ToString(), title.Length > 0 ? title : id.ToString()));
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

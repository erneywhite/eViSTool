using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Notifications;

/// <summary>Сообщение для Discord: от кого (имя на сообщении) и текст.</summary>
public sealed record ChatPost(string Author, string Text);

/// <summary>
/// Чат игры → Discord. Строки чата сервер пишет так: «[Server Chat] 0 | Erney: привет» (в логе — «[Chat] 0 | …»),
/// 0 — общий чат; сообщения других групп (личные группы игроков) не пересылаем. Сообщение уходит от имени игрока —
/// вебхук Discord позволяет подписать каждое сообщение своим именем. Упоминания (@everyone, @роль) выключены:
/// написанное в игре не должно звать людей в Discord.
/// </summary>
public static partial class ChatBridge
{
    [GeneratedRegex(@"\[(?:Server )?Chat\] (?<group>\d+) \| (?<name>[^:]{1,40}): (?<text>.*)$")]
    private static partial Regex ChatLine();

    /// <summary>Сообщение общего чата игрока; иначе (не чат, другая группа, сводка смерти) — null.</summary>
    public static ChatPost? Parse(string line)
    {
        var m = ChatLine().Match(line);
        if (!m.Success || m.Groups["group"].Value != "0") return null;
        var text = m.Groups["text"].Value.Trim();
        return text.Length == 0 ? null : new ChatPost(m.Groups["name"].Value.Trim(), text);
    }

    /// <summary>
    /// Склеить идущие подряд сообщения одного автора (Discord пускает не больше ~30 сообщений в минуту на вебхук):
    /// «Erney: дай блоки» + «Erney: ЗАБЕРИ» → одно сообщение в две строки. Длина — не больше лимита Discord.
    /// </summary>
    public static List<ChatPost> Batch(IEnumerable<ChatPost> posts, int maxLength = 1900)
    {
        var result = new List<ChatPost>();
        foreach (var p in posts)
        {
            if (result.Count > 0 && result[^1].Author == p.Author && result[^1].Text.Length + 1 + p.Text.Length <= maxLength)
                result[^1] = result[^1] with { Text = result[^1].Text + "\n" + p.Text };
            else
                result.Add(p with { Text = p.Text.Length > maxLength ? p.Text[..maxLength] : p.Text });
        }
        return result;
    }

    /// <summary>Тело запроса вебхука: имя автора, текст, без упоминаний.</summary>
    public static string Body(ChatPost post) => new JObject
    {
        // в имени вебхука Discord не пускает «discord» и пустое — подстрахуемся
        ["username"] = string.IsNullOrWhiteSpace(post.Author) ? "eViSTool" : post.Author.Replace("discord", "d1scord", StringComparison.OrdinalIgnoreCase),
        ["content"] = post.Text,
        ["allowed_mentions"] = new JObject { ["parse"] = new JArray() },
    }.ToString(Formatting.None);
}

/// <summary>
/// Очередь чата для Discord: строки копятся и уходят пачкой раз в пару секунд (подряд от одного автора — одним
/// сообщением), на «слишком часто» Discord отвечает 429 — ждём, сколько он просит, и пробуем снова. Ошибки — в report.
/// </summary>
public sealed class ChatRelay(Func<NotifyChannel?> channel, HttpClient http, Action<string> report)
{
    private readonly Queue<ChatPost> _queue = new();
    private readonly object _lock = new();
    private bool _flushing;

    public void Post(ChatPost post)
    {
        lock (_lock)
        {
            if (_queue.Count > 500) return; // Discord недоступен давно — не копим без конца
            _queue.Enqueue(post);
            if (_flushing) return;
            _flushing = true;
        }
        _ = FlushLaterAsync();
    }

    private async Task FlushLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        while (true)
        {
            List<ChatPost> batch;
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    _flushing = false;
                    return;
                }
                batch = ChatBridge.Batch(_queue);
                _queue.Clear();
            }
            if (channel() is not { Secret: { } url }) continue; // выключили, пока копилось
            foreach (var post in batch) await SendAsync(url, post).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(string url, ChatPost post)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var content = new StringContent(ChatBridge.Body(post), Encoding.UTF8, "application/json");
                using var resp = await http.PostAsync(url, content).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return;
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    await Task.Delay(wait < TimeSpan.FromSeconds(30) ? wait : TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    continue;
                }
                report(Localization.Loc.T("chat.failed", (int)resp.StatusCode));
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // в тексте исключения может оказаться адрес вебхука с ключом — пересказываем своими словами
                report(Localization.Loc.T("notify.network", "discord.com"));
                return;
            }
        }
    }
}

using System.Net;
using System.Net.Http;
using eViSTool.Core.Notifications;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests;

/// <summary>Оповещения в Telegram — на подставном HTTP: что уходит, как понимаются ошибки, токен не утекает.</summary>
public sealed class NotifierTests
{
    private const string Token = "123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw";

    private sealed class Fake(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public string? Url;
        public JObject? Sent;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Url = request.RequestUri!.ToString();
            Sent = JObject.Parse(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(code) { Content = new StringContent(body) };
        }
    }

    private sealed class Down : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("No such host: " + request.RequestUri);
    }

    private static NotifyChannel Telegram(string? chat = "42") => new()
    {
        Name = "tg", Kind = NotifyKind.Telegram, SecretProtected = NotifySecret.Protect(Token), ChatId = chat,
    };

    [Fact]
    public async Task Telegram_SendsPlainTextToTheChat()
    {
        var fake = new Fake(HttpStatusCode.OK, """{ "ok": true, "result": {} }""");
        await new Notifier(new HttpClient(fake)).SendAsync(Telegram(), new NotifyMessage("Server crashed", "mod_x*1.0"));

        Assert.Equal($"https://api.telegram.org/bot{Token}/sendMessage", fake.Url);
        Assert.Equal("42", fake.Sent!.Value<string>("chat_id"));
        Assert.Equal("Server crashed\nmod_x*1.0", fake.Sent.Value<string>("text"));
        Assert.Null(fake.Sent["parse_mode"]); // без разметки: * и _ в именах модов не ломают сообщение
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{ "ok": false, "error_code": 401, "description": "Unauthorized" }""")]
    [InlineData(HttpStatusCode.BadRequest, """{ "ok": false, "error_code": 400, "description": "Bad Request: chat not found 123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw" }""")]
    public async Task Telegram_Errors_AreExplained_WithoutTheToken(HttpStatusCode code, string body)
    {
        var ex = await Assert.ThrowsAsync<NotifyException>(() =>
            new Notifier(new HttpClient(new Fake(code, body))).SendAsync(Telegram(), new NotifyMessage("t", "x")));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.DoesNotContain("AAHdqTcv", ex.Message);
    }

    [Fact]
    public async Task Telegram_NetworkError_DoesNotLeakTheTokenFromTheUrl()
    {
        var ex = await Assert.ThrowsAsync<NotifyException>(() =>
            new Notifier(new HttpClient(new Down())).SendAsync(Telegram(), new NotifyMessage("t", "x")));

        Assert.DoesNotContain("AAHdqTcv", ex.Message);
        Assert.Contains("api.telegram.org", ex.Message);
    }

    [Fact]
    public async Task Telegram_NoChat_IsRefusedBeforeSending()
    {
        var fake = new Fake(HttpStatusCode.OK, """{ "ok": true }""");
        await Assert.ThrowsAsync<NotifyException>(() => new Notifier(new HttpClient(fake)).SendAsync(Telegram(chat: null), new NotifyMessage("t", "x")));
        Assert.Null(fake.Url);
    }

    [Fact]
    public async Task Telegram_FindChats_NewestFirst_WithReadableTitles()
    {
        var fake = new Fake(HttpStatusCode.OK, """
            { "ok": true, "result": [
              { "update_id": 1, "message": { "chat": { "id": 111, "type": "private", "first_name": "Erney", "username": "erney" }, "text": "hi" } },
              { "update_id": 2, "message": { "chat": { "id": -100222, "type": "supergroup", "title": "VS server" }, "text": "hi" } },
              { "update_id": 3, "message": { "chat": { "id": 111, "type": "private", "first_name": "Erney" }, "text": "again" } }
            ] }
            """);

        var chats = await new Notifier(new HttpClient(fake)).FindTelegramChatsAsync(Token);

        Assert.Equal(["111", "-100222"], chats.Select(c => c.Id));
        Assert.Equal("Erney", chats[0].Title);
        Assert.Equal("VS server", chats[1].Title);
        Assert.EndsWith("/getUpdates", fake.Url);
    }

    [Fact]
    public async Task Telegram_ForumTopics_AreFoundSeparately_AndSentTo()
    {
        var fake = new Fake(HttpStatusCode.OK, """
            { "ok": true, "result": [
              { "update_id": 1, "message": { "chat": { "id": -100333, "type": "supergroup", "title": "VS", "is_forum": true }, "text": "general" } },
              { "update_id": 2, "message": { "chat": { "id": -100333, "type": "supergroup", "title": "VS", "is_forum": true },
                  "message_thread_id": 7, "is_topic_message": true, "text": "here",
                  "reply_to_message": { "message_id": 7, "forum_topic_created": { "name": "Анонсы" } } } },
              { "update_id": 3, "channel_post": { "chat": { "id": -100444, "type": "channel", "title": "News" }, "text": "post" } }
            ] }
            """);
        var notifier = new Notifier(new HttpClient(fake));

        var chats = await notifier.FindTelegramChatsAsync(Token);
        Assert.Equal(["News", "VS › Анонсы", "VS"], chats.Select(c => c.Title));
        Assert.Equal(7, chats[1].TopicId);
        Assert.Null(chats[2].TopicId);

        await notifier.SendAsync(Telegram("-100333") with { TopicId = 7 }, new NotifyMessage("t", "x"));
        Assert.Equal(7, fake.Sent!.Value<long>("message_thread_id"));
    }

    [Fact]
    public async Task Telegram_BadToken_IsRefusedWithoutARequest()
    {
        var fake = new Fake(HttpStatusCode.OK, "{}");
        await Assert.ThrowsAsync<NotifyException>(() => new Notifier(new HttpClient(fake)).FindTelegramChatsAsync("not a token"));
        Assert.Null(fake.Url);
    }

    [Fact]
    public void Channels_AreSavedWithTheSecretEncrypted()
    {
        var root = Path.Combine(Path.GetTempPath(), "evistool-notify-" + Guid.NewGuid().ToString("N"));
        try
        {
            NotifyChannels.Save(root, [Telegram()]);
            Assert.DoesNotContain("AAHdqTcv", File.ReadAllText(NotifyChannels.FileIn(root)));
            var back = Assert.Single(NotifyChannels.Load(root));
            Assert.Equal(Token, back.Secret);
            Assert.Equal("42", back.ChatId);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}

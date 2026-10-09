using System.Net;
using System.Net.Http;
using eViSTool.Core.Notifications;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests;

/// <summary>Чат игры → Discord: какие строки сервера считаются чатом, склейка, тело вебхука, очередь.</summary>
public sealed class ChatBridgeTests
{
    [Theory]
    [InlineData("8.10.2026 20:31:05 [Server Chat] 0 | toristarm: куда тебе тут расширять?", "toristarm", "куда тебе тут расширять?")]
    [InlineData("8.10.2026 20:31:05 [Chat] 0 | Erney: привет: как дела", "Erney", "привет: как дела")]
    [InlineData("[Chat] 0 | Erney: h̷̢e̵l̶l̸o̴", "Erney", "h̷̢e̵l̶l̸o̴")] // во время темпорального шторма текст в залго — оставляем как есть
    public void Parse_GeneralChat(string line, string author, string text)
    {
        var post = ChatBridge.Parse(line);
        Assert.Equal(new ChatPost(author, text), post);
    }

    [Theory]
    [InlineData("8.10.2026 20:31:05 [Chat] 3 | Erney: в своей группе")]               // не общий чат
    [InlineData("8.10.2026 20:31:05 [Chat] Death Recap: Erney was killed by a wolf")] // сводка смерти
    [InlineData("8.10.2026 20:31:05 [Server Event] Erney 192.168.31.220:60489 joins.")]
    [InlineData("8.10.2026 20:31:05 [Chat] 0 | Erney:   ")]
    [InlineData("")]
    public void Parse_NotChat(string line) => Assert.Null(ChatBridge.Parse(line));

    [Fact]
    public void Batch_MergesSameAuthorInARow()
    {
        var batch = ChatBridge.Batch([new("A", "1"), new("A", "2"), new("B", "3"), new("A", "4")]);
        Assert.Equal([new ChatPost("A", "1\n2"), new ChatPost("B", "3"), new ChatPost("A", "4")], batch);
    }

    [Fact]
    public void Batch_RespectsLength()
    {
        var batch = ChatBridge.Batch([new("A", new string('x', 15)), new("A", new string('y', 15)), new("A", new string('z', 40))], maxLength: 20);
        Assert.Equal(3, batch.Count);
        Assert.Equal(20, batch[2].Text.Length);
    }

    [Fact]
    public void Body_NoMentions_AndSafeName()
    {
        var body = JObject.Parse(ChatBridge.Body(new ChatPost("MyDiscordFan", "@everyone смотрите")));
        Assert.Equal("Myd1scordFan", (string?)body["username"]);
        Assert.Equal("@everyone смотрите", (string?)body["content"]);
        Assert.Empty((JArray)body["allowed_mentions"]!["parse"]!);
        Assert.Equal("eViSTool", (string?)JObject.Parse(ChatBridge.Body(new ChatPost(" ", "x")))["username"]);
    }

    private sealed class Fake : HttpMessageHandler
    {
        public readonly List<JObject> Sent = [];
        public readonly Queue<HttpStatusCode> Codes = new();
        public readonly TaskCompletionSource Done = new();
        public int Expect = 1;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var code = Codes.Count > 0 ? Codes.Dequeue() : HttpStatusCode.NoContent;
            var resp = new HttpResponseMessage(code);
            if (code == HttpStatusCode.TooManyRequests) resp.Headers.RetryAfter = new(TimeSpan.FromMilliseconds(50));
            else
            {
                var json = JObject.Parse(await request.Content!.ReadAsStringAsync(ct));
                lock (Sent) Sent.Add(json);
                if (Sent.Count >= Expect) Done.TrySetResult();
            }
            return resp;
        }
    }

    private static NotifyChannel Discord() => new() { Id = "d", Name = "D", Kind = NotifyKind.Discord, SecretProtected = NotifySecret.Protect("https://discord.com/api/webhooks/1/abc") };

    [Fact]
    public async Task Relay_BatchesAndRetriesOn429()
    {
        var fake = new Fake { Expect = 2 };
        fake.Codes.Enqueue(HttpStatusCode.TooManyRequests);
        var errors = new List<string>();
        var relay = new ChatRelay(Discord, new HttpClient(fake), errors.Add);
        relay.Post(new("Erney", "дай блоки"));
        relay.Post(new("Erney", "ЗАБЕРИ"));
        relay.Post(new("toristarm", "ок"));
        await fake.Done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("дай блоки\nЗАБЕРИ", (string?)fake.Sent[0]["content"]);
        Assert.Equal("toristarm", (string?)fake.Sent[1]["username"]);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Relay_ReportsErrorWithoutUrl()
    {
        var fake = new Fake();
        fake.Codes.Enqueue(HttpStatusCode.NotFound);
        var reported = new TaskCompletionSource<string>();
        var relay = new ChatRelay(Discord, new HttpClient(fake), e => reported.TrySetResult(e));
        relay.Post(new("Erney", "x"));
        var error = await reported.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("404", error);
        Assert.DoesNotContain("abc", error);
    }
}

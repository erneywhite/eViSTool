using System.Net;
using System.Net.Http;
using eViSTool.Core.Notifications;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests;

/// <summary>Discord, ntfy и свой вебхук — на подставном HTTP.</summary>
public sealed class NotifierWebTests
{
    private const string DiscordHook = "https://discord.com/api/webhooks/123456789012345678/AbCdEf_secret-Token";

    private sealed class Fake(HttpStatusCode code = HttpStatusCode.NoContent) : HttpMessageHandler
    {
        public string? Url;
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Url = request.RequestUri!.ToString();
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(code) { Content = new StringContent("") };
        }
    }

    private static readonly NotifyMessage Crash = new(NotifySeverity.Problem, "Survival", "Server crashed",
        [new NotifyLine("⚑", "Mod", "carry_on*2 (beta)"), new NotifyLine("↻", "Watchdog", "again in 9 s")]);

    private static NotifyChannel Channel(NotifyKind kind, string secret, string? url = null, string? template = null) => new()
    {
        Name = kind.ToString(), Kind = kind, SecretProtected = NotifySecret.Protect(secret), Url = url, Template = template,
    };

    [Fact]
    public async Task Discord_EmbedWithColorServerAndEscapedDetails()
    {
        var fake = new Fake();
        await new Notifier(new HttpClient(fake)).SendAsync(Channel(NotifyKind.Discord, DiscordHook), Crash);

        Assert.Equal(DiscordHook, fake.Url);
        var embed = JObject.Parse(fake.Body!)["embeds"]![0]!;
        Assert.Equal(0xE24B4A, embed.Value<int>("color"));
        Assert.Equal("Survival", embed["author"]!.Value<string>("name"));
        Assert.Equal("Server crashed", embed.Value<string>("title"));
        Assert.Equal("⚑ *Mod:* carry\\_on\\*2 \\(beta\\)\n↻ *Watchdog:* again in 9 s", embed.Value<string>("description"));
    }

    [Fact]
    public async Task Discord_WrongLink_IsRefused_DeletedHook_IsExplainedWithoutTheToken()
    {
        await Assert.ThrowsAsync<NotifyException>(() =>
            new Notifier(new HttpClient(new Fake())).SendAsync(Channel(NotifyKind.Discord, "https://example.com/x"), Crash));

        var ex = await Assert.ThrowsAsync<NotifyException>(() =>
            new Notifier(new HttpClient(new Fake(HttpStatusCode.NotFound))).SendAsync(Channel(NotifyKind.Discord, DiscordHook), Crash));
        Assert.DoesNotContain("secret-Token", ex.Message);
    }

    [Fact]
    public async Task Ntfy_TitlePriorityAndColorTag_ToTheChosenServer()
    {
        var fake = new Fake(HttpStatusCode.OK);
        await new Notifier(new HttpClient(fake)).SendAsync(Channel(NotifyKind.Ntfy, "evistool-abc", url: "https://ntfy.example.org/"), Crash);

        Assert.Equal("https://ntfy.example.org/", fake.Url);
        var body = JObject.Parse(fake.Body!);
        Assert.Equal("evistool-abc", body.Value<string>("topic"));
        Assert.Equal("Survival · Server crashed", body.Value<string>("title"));
        Assert.Equal("⚑ Mod: carry_on*2 (beta)\n↻ Watchdog: again in 9 s", body.Value<string>("message"));
        Assert.Equal(4, body.Value<int>("priority"));
        Assert.Equal("red_circle", body["tags"]![0]!.Value<string>());
    }

    [Fact]
    public async Task Ntfy_DefaultServer_AndTopicCheck()
    {
        var fake = new Fake(HttpStatusCode.OK);
        await new Notifier(new HttpClient(fake)).SendAsync(Channel(NotifyKind.Ntfy, Notifier.NewNtfyTopic()), Crash);
        Assert.StartsWith("https://ntfy.sh", fake.Url);

        await Assert.ThrowsAsync<NotifyException>(() =>
            new Notifier(new HttpClient(new Fake())).SendAsync(Channel(NotifyKind.Ntfy, "bad topic/../x"), Crash));
    }

    [Fact]
    public async Task Webhook_TemplateValuesAreJsonEscaped()
    {
        var fake = new Fake(HttpStatusCode.OK);
        var quoted = Crash with { Title = "Server \"crashed\"\nagain" };
        await new Notifier(new HttpClient(fake)).SendAsync(Channel(NotifyKind.Webhook, "https://hooks.example.org/x?key=123", template: WebhookTemplates.Slack), quoted);

        Assert.Equal("https://hooks.example.org/x?key=123", fake.Url);
        var text = JObject.Parse(fake.Body!).Value<string>("text")!;
        Assert.Contains("Server \"crashed\"\nagain", text);
        Assert.StartsWith("🔴 Survival", text);
    }

    [Fact]
    public void Webhook_DefaultTemplate_HasAllFields_BrokenTemplate_IsExplained()
    {
        var json = JObject.Parse(Notifier.WebhookBody(null, Crash, new DateTime(2026, 10, 9, 14, 0, 0)));
        Assert.Equal("problem", json.Value<string>("severity"));
        Assert.Equal("Survival", json.Value<string>("server"));
        Assert.Equal("2026-10-09 14:00:00", json.Value<string>("time"));

        Assert.Throws<NotifyException>(() => Notifier.WebhookBody("""{ "text": {text} }""", Crash, DateTime.Now));
    }

    [Fact]
    public async Task Webhook_ServerError_NamesTheHostOnly()
    {
        var ex = await Assert.ThrowsAsync<NotifyException>(() => new Notifier(new HttpClient(new Fake(HttpStatusCode.InternalServerError)))
            .SendAsync(Channel(NotifyKind.Webhook, "https://hooks.example.org/x?key=SECRET123"), Crash));
        Assert.Contains("hooks.example.org", ex.Message);
        Assert.DoesNotContain("SECRET123", ex.Message);
    }
}

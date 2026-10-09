using System.Net;
using System.Net.Http;
using eViSTool.Core.Notifications;

namespace eViSTool.Core.Tests;

/// <summary>Оповещения сервера: какое событие куда уходит, и как секреты ездят к агенту.</summary>
public sealed class ServerNotifyTests
{
    private const string TokenA = "111111111:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string TokenB = "222222222:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    private sealed class Recorder : HttpMessageHandler
    {
        public readonly List<string> Urls = [];
        public readonly List<string> Bodies = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Urls) Urls.Add(request.RequestUri!.ToString());
            var body = await request.Content!.ReadAsStringAsync(ct);
            lock (Bodies) Bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "ok": true, "result": {} }""") };
        }
    }

    private static NotifyChannel Channel(string id, string token) => new()
    {
        Id = id, Name = id, Kind = NotifyKind.Telegram, SecretProtected = NotifySecret.Protect(token), ChatId = "1",
    };

    private static ServerNotifySettings Settings() => new()
    {
        ServerName = "Survival",
        Channels = [Channel("a", TokenA), Channel("b", TokenB)],
        Routes = new() { [NotifyEvent.ServerCrashed] = ["a", "b"], [NotifyEvent.PlayerJoined] = ["b"] },
    };

    [Fact]
    public async Task Event_GoesOnlyToItsChannels_WithTheServerName()
    {
        var http = new Recorder();
        var notifier = new ServerNotifier(Settings, new HttpClient(http), (_, _) => { });

        notifier.Notify(NotifyEvent.PlayerJoined, "Anna joined", "Online: 1");
        notifier.Notify(NotifyEvent.ServerStopped, "server stopped", ""); // ни в один канал не включено
        for (var i = 0; i < 50 && http.Urls.Count < 1; i++) await Task.Delay(20);
        await Task.Delay(100);

        Assert.Single(http.Urls);
        Assert.Contains(TokenB, http.Urls[0]);
        Assert.Contains("Survival: Anna joined", http.Bodies[0]);
    }

    [Fact]
    public async Task Test_GoesToEveryChannelInUse()
    {
        var http = new Recorder();
        var errors = await new ServerNotifier(Settings, new HttpClient(http), (_, _) => { }).TestAsync("t", "x");

        Assert.Empty(errors);
        Assert.Equal(2, http.Urls.Count);
    }

    [Fact]
    public async Task Failure_IsReportedPerChannel_WithoutTheSecret()
    {
        var reported = new List<string>();
        var broken = Settings() with { Channels = [Channel("a", TokenA) with { ChatId = null }] };
        var errors = await new ServerNotifier(() => broken, new HttpClient(new Recorder()), (name, error) => reported.Add(name + ": " + error))
            .TestAsync("t", "x");

        Assert.Single(errors);
        Assert.Single(reported);
        Assert.DoesNotContain("AAAAAAAA", reported[0]);
    }

    [Fact]
    public void Upload_CarriesTheSecret_AndTheAgentEncryptsItAgain()
    {
        var upload = Settings().ToUpload();
        Assert.Equal(TokenA, upload.Channels[0].Secret);
        Assert.Null(upload.Channels[0].Channel.SecretProtected);

        var onAgent = upload.ToSettings();
        Assert.Equal(TokenA, onAgent.Channels[0].Secret);
        Assert.True(onAgent.IsOn(NotifyEvent.ServerCrashed, "a"));
        Assert.False(onAgent.IsOn(NotifyEvent.PlayerJoined, "a"));

        // окну на другой машине — без секретов
        Assert.All(onAgent.WithoutSecrets().Channels, c => Assert.Null(c.SecretProtected));
    }

    [Fact]
    public void SavedFile_HasEventNames_AndNoPlainSecrets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "evistool-notify-srv-" + Guid.NewGuid().ToString("N"));
        try
        {
            Settings().Save("p1", dir);
            var text = File.ReadAllText(ServerNotifySettings.FileFor("p1", dir));
            Assert.Contains("\"ServerCrashed\"", text);
            Assert.DoesNotContain("AAAAAAAA", text);
            Assert.True(ServerNotifySettings.Load("p1", dir).IsOn(NotifyEvent.PlayerJoined, "b"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}

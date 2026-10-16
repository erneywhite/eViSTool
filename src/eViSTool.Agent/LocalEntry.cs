using eViSTool.Core.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Локальный вход: только 127.0.0.1, свободный порт (его выберет система), ключ профиля на каждом запросе.
/// Его адрес агент пишет в data/agents/&lt;id&gt;.json — по нему окно на этой машине и находит агента.
/// </summary>
internal sealed class LocalEntry
{
    private readonly WebApplication _app;

    public LocalEntry(AgentState state, string key, RemoteEntry remote, AgentApi api)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 1L << 30; // архив мода бывает и сотни мегабайт
            k.Listen(System.Net.IPAddress.Loopback, 0); // свободный порт выберет система
        });
        _app = builder.Build();

        // ключ — на каждом запросе. При включённом удалённом доступе запросы окна с этой машины держат агента живым:
        // окно открыто — к серверу можно подключиться снаружи. Доступ выключен — агент, как и прежде, уходит после простоя.
        _app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Headers.TryGetValue(AgentProtocol.KeyHeader, out var got) || got != key)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (remote.Enabled) state.LastActivity = DateTime.Now;
            await next();
        });

        api.Map(_app, isRemote: false);
    }

    /// <summary>Начать слушать; ответ — порт, который выбрала система.</summary>
    public async Task<int> StartAsync()
    {
        await _app.StartAsync();
        return new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
    }

    public Task StopAsync() => _app.StopAsync();
}

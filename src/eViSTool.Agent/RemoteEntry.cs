using System.Security.Cryptography;
using System.Text;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// Удалённый доступ: второй вход — из сети, по TLS и со своим ключом. Включается и выключается файлом настроек: окно
/// пишет его, агент подхватывает сам (<see cref="ApplyAsync"/> из главного цикла). Неверный ключ несколько раз подряд —
/// адрес ждёт минуту.
/// </summary>
internal sealed class RemoteEntry(AgentState state, AgentJournal journal)
{
    private readonly string _file = RemoteAccess.FileFor(state.Options.ProfileId, state.Options.AgentsDir);
    private readonly Dictionary<string, (int Count, DateTime BlockedUntil)> _failures = new();
    private RemoteSettings _settings = new();
    private WebApplication? _app;
    private DateTime? _stamp;
    private AgentApi? _api;

    /// <summary>Удалённый доступ включён в настройках: тогда запросы окна с этой машины держат агента живым.</summary>
    public bool Enabled => _settings.Enabled;

    /// <summary>Порт, на котором вход слушает; null — не слушает.</summary>
    public int? Port => _app is not null ? _settings.Port : null;

    /// <summary>Почему вход не поднялся (null — поднялся или выключен).</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Точки API для этого входа — те же, что у локального. Задаётся после создания API: тому для статуса нужны
    /// порт и ошибка этого входа. Вызвать до первого <see cref="ApplyAsync"/>.
    /// </summary>
    public void Serve(AgentApi agentApi) => _api = agentApi;

    /// <summary>Поднять, пересоздать или погасить вход по файлу настроек; файл не менялся — ничего не делать.</summary>
    /// <exception cref="InvalidOperationException"><see cref="Serve"/> не вызван: это ошибка сборки агента.</exception>
    public async Task ApplyAsync()
    {
        // Забытый Serve проверяем до всего остального: агент упадёт при первом же вызове, то есть при запуске. Иначе сбой
        // всплыл бы только при включении удалённого доступа, и тихо: метка файла уже записана, следующий тик ничего
        // не делает, а исключение не попадает в фильтр catch ниже
        var api = _api ?? throw new InvalidOperationException("RemoteEntry.Serve не вызван до ApplyAsync");
        var host = state.Host;
        var opts = state.Options;
        var changedAt = File.Exists(_file) ? File.GetLastWriteTimeUtc(_file) : default;
        if (changedAt == _stamp) return;
        _stamp = changedAt;

        var wasPort = _app is not null ? _settings.Port : (int?)null;
        _settings = RemoteAccess.Load(opts.ProfileId, opts.AgentsDir);
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
        Error = null;
        if (!_settings.Enabled || _settings.Port <= 0 || _settings.Key.Length < 32)
        {
            // выключили — видно и в консоли: снаружи больше не подключиться
            if (wasPort is { } closed) host.Console.Add(ConsoleLineKind.System, Loc.T("remote.closed", closed));
            return;
        }

        try
        {
            var cert = RemoteAccess.EnsureCertificate(opts.ProfileId, opts.AgentsDir);
            var remoteKey = Encoding.UTF8.GetBytes(_settings.Key);
            var rb = WebApplication.CreateSlimBuilder();
            rb.Logging.ClearProviders();
            rb.WebHost.UseKestrelHttpsConfiguration();
            rb.WebHost.UseKestrel(k =>
            {
                k.Limits.MaxRequestBodySize = 1L << 30; // архив мода бывает и сотни мегабайт
                k.Listen(System.Net.IPAddress.Any, _settings.Port, o => o.UseHttps(cert));
            });
            var web = rb.Build();
            web.Use(async (ctx, next) =>
            {
                // неверный ключ несколько раз подряд — адрес ждёт минуту: подбирать ключ бессмысленно, но и шуметь незачем
                var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
                lock (_failures)
                {
                    if (_failures.TryGetValue(ip, out var f) && f.BlockedUntil > DateTime.Now)
                    {
                        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                        return;
                    }
                }
                var got = ctx.Request.Headers.TryGetValue(AgentProtocol.KeyHeader, out var header) ? Encoding.UTF8.GetBytes(header.ToString()) : [];
                if (!CryptographicOperations.FixedTimeEquals(got, remoteKey))
                {
                    int count;
                    lock (_failures)
                    {
                        var f = _failures.GetValueOrDefault(ip);
                        count = f.Count + 1;
                        _failures[ip] = count >= 5 ? (0, DateTime.Now.AddMinutes(1)) : (count, default);
                    }
                    // в журнал — первая неудача с адреса и блокировка после пяти, а не каждая попытка
                    if (count == 1) journal.Write("authFailed", [], ctx, isRemote: true);
                    else if (count >= 5) journal.Write("authBlocked", [], ctx, isRemote: true);
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                lock (_failures) _failures.Remove(ip);
                await next();
            });
            api.Map(web, isRemote: true);
            await web.StartAsync();
            _app = web;
            host.Console.Add(ConsoleLineKind.System, Loc.T("remote.listening", _settings.Port));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or CryptographicException
                                       or System.Net.Sockets.SocketException)
        {
            Error = ex.Message;
            host.Console.Add(ConsoleLineKind.System, Loc.T("remote.failed", _settings.Port, ex.Message));
        }
    }

    /// <summary>При выходе агента: перестать слушать.</summary>
    public async Task StopAsync()
    {
        if (_app is not null) await _app.StopAsync();
    }
}

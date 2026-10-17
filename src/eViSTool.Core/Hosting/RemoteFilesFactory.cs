namespace eViSTool.Core.Hosting;

/// <summary>
/// Откуда берутся соединения. Core про FluentFTP не знает: транспорт регистрирует сборка eViSTool.Hosting при запуске
/// окна. Тесты подменяют транспорт для своего адреса (<see cref="RegisterHost"/>): тесты Core идут параллельно,
/// и одна общая подмена мешала бы соседям.
/// </summary>
public static class RemoteFilesFactory
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<HostedProtocol, Func<HostedEndpoint, IRemoteFiles>> ByProtocol = [];
    private static readonly Dictionary<string, Func<HostedEndpoint, IRemoteFiles>> ByHost = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Транспорт для протокола (повторная регистрация заменяет прежний).</summary>
    public static void Register(HostedProtocol protocol, Func<HostedEndpoint, IRemoteFiles> create)
    {
        lock (Gate) ByProtocol[protocol] = create;
    }

    /// <summary>Подмена для одного адреса — для тестов. Dispose снимает подмену.</summary>
    public static IDisposable RegisterHost(string host, Func<HostedEndpoint, IRemoteFiles> create)
    {
        lock (Gate) ByHost[host] = create;
        return new Registration(host, create);
    }

    public static bool Supports(HostedProtocol protocol)
    {
        lock (Gate) return ByProtocol.ContainsKey(protocol);
    }

    /// <summary>Новое, ещё не подключённое соединение. Транспорта нет — <see cref="RemoteFailure.Unsupported"/>.</summary>
    public static IRemoteFiles Create(HostedEndpoint endpoint)
    {
        Func<HostedEndpoint, IRemoteFiles>? create;
        lock (Gate)
        {
            if (!ByHost.TryGetValue(endpoint.Host, out create)) ByProtocol.TryGetValue(endpoint.Protocol, out create);
        }
        return create is not null ? create(endpoint)
            : throw new RemoteFilesException(RemoteFailure.Unsupported, $"no transport for {endpoint.Protocol}");
    }

    private sealed class Registration(string host, Func<HostedEndpoint, IRemoteFiles> create) : IDisposable
    {
        public void Dispose()
        {
            // снимаем только свою подмену: если адрес успели перерегистрировать, чужую не трогаем
            lock (Gate)
                if (ByHost.TryGetValue(host, out var current) && current == create) ByHost.Remove(host);
        }
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server.Remote;

namespace eViSTool.Core.Tests;

/// <summary>
/// Выключенный удалённый сервер не тормозит установку модов: проверка «Установить также в» помнит, что он не ответил,
/// и пару минут не стучится к нему снова — профиль сразу «нет связи».
/// </summary>
public sealed class UnreachableRemoteTests
{
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task OfferCheck_RemembersAServerThatDidNotAnswer()
    {
        var target = new ModTarget(new GameProfile { Kind = ProfileKind.Server, Name = "Linux" },
            new ConnectionCode("127.0.0.1", ClosedPort(), "key", "fingerprint"));
        Assert.False(ModTargets.RecentlyUnreachable(target));

        await Assert.ThrowsAnyAsync<Exception>(() => ModTargets.ScanForOfferAsync(target));
        Assert.True(ModTargets.RecentlyUnreachable(target));

        // второй раз — без сети: та же ошибка сразу
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => ModTargets.ScanForOfferAsync(target));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), watch.Elapsed.ToString());

        // и в окне выбора профиль — «нет связи»
        var option = await AlsoInstall.EvaluateAsync(target, "carryon", [], new eViSTool.Core.ModDb.ModDbRelease());
        Assert.Equal(AlsoInstallState.Unavailable, option.State);
    }
}

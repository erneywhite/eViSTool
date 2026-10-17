using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>Пул соединений с серверами у хостера: одно на профиль, по очереди, повтор после обрыва — только у чтения.</summary>
public sealed class HostedSessionsTests : IDisposable
{
    private readonly HostedFixture _host = new();

    public HostedSessionsTests()
    {
        _host.GPortalLayout();
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task OneConnectionServesSeveralOperations()
    {
        await ModTargets.ScanAsync(_host.Target);
        await ModTargets.StampAsync(_host.Target);
        await ModTargets.ScanAsync(_host.Target);

        Assert.Equal(1, _host.Server.Connects);
    }

    [Fact]
    public async Task OperationsOfOneProfileRunOneAtATime()
    {
        var inside = 0;
        var most = 0;
        await HostedSessions.UseAsync(_host.Profile.Id, _host.Endpoint, _ => Task.FromResult(0), default); // соединение открыто

        var work = Enumerable.Range(0, 5).Select(_ => HostedSessions.UseAsync(_host.Profile.Id, _host.Endpoint, async files =>
        {
            var now = Interlocked.Increment(ref inside);
            lock (_host) most = Math.Max(most, now);
            await Task.Delay(20);
            await files.ListAsync("/Mods", default);
            Interlocked.Decrement(ref inside);
            return true;
        }, default));
        await Task.WhenAll(work);

        Assert.Equal(1, most);
    }

    [Fact]
    public async Task ReadIsRetriedOnceAfterTheConnectionDrops()
    {
        await ModTargets.ScanAsync(_host.Target);
        _host.Server.DropNext = true;

        var (_, mods) = await ModTargets.ScanAsync(_host.Target);

        Assert.Contains(mods, m => m.Info?.ModId == "alpha");
        Assert.Equal(2, _host.Server.Connects);
    }

    [Fact]
    public async Task WriteIsNotRetried_ButTheNextOperationReconnects()
    {
        var (_, mods) = await ModTargets.ScanAsync(_host.Target);
        var writes = _host.Server.Writes.Count;
        _host.Server.DropNext = true;

        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() =>
            ModTargets.SetEnabledAsync(_host.Target, mods.Single(m => m.Info?.ModId == "alpha"), enabled: false));

        Assert.Equal(RemoteFailure.Connect, ex.Kind);
        Assert.Equal(writes, _host.Server.Writes.Count);
        await ModTargets.SetEnabledAsync(_host.Target, mods.Single(m => m.Info?.ModId == "alpha"), enabled: false);
        Assert.Equal(2, _host.Server.Connects);
    }

    [Fact]
    public async Task CancelledOperation_DoesNotLeaveItsConnectionInThePool()
    {
        await ModTargets.ScanAsync(_host.Target);
        using var cts = new CancellationTokenSource();

        // отмена посреди передачи: адаптер ещё считает соединение живым, а сервер его уже уронил
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HostedSessions.UseAsync<int>(_host.Profile.Id, _host.Endpoint, _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }, cts.Token));

        await ModTargets.ScanAsync(_host.Target);
        Assert.Equal(2, _host.Server.Connects);
    }

    [Fact]
    public async Task ChangedPassword_OpensANewConnection()
    {
        await ModTargets.ScanAsync(_host.Target);
        _host.Settings.Password = HostedSecret.Protect("new password");

        await ModTargets.ScanAsync(_host.Target);

        Assert.Equal(2, _host.Server.Connects);
    }

    [Fact]
    public async Task Forget_ClosesTheConnection()
    {
        await ModTargets.ScanAsync(_host.Target);
        Assert.True(_host.Server.IsConnected);

        HostedSessions.Forget(_host.Profile.Id);
        for (var i = 0; i < 50 && _host.Server.IsConnected; i++) await Task.Delay(10);

        Assert.False(_host.Server.IsConnected);
        await ModTargets.ScanAsync(_host.Target);
        Assert.Equal(2, _host.Server.Connects);
    }

    [Fact]
    public async Task LoginFailure_ComesOutAsRemoteFilesException_WithWords()
    {
        _host.Server.FailOn = (op, _) => op == "connect";
        _host.Server.FailKind = RemoteFailure.Auth;

        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => ModTargets.ScanAsync(_host.Target));

        HostedTestText.AssertIs("hosted.errAuth", ModTargets.Describe(_host.Target, ex));
        Assert.IsAssignableFrom<IOException>(ex); // окно ловит IOException — в журнал вылетов это не попадёт
    }
}

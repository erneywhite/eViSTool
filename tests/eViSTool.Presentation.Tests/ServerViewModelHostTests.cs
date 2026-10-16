using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Раздел «Сервер» как хозяин наблюдателя: что он показывает, когда наблюдатель сообщает о статусе, пропаже связи,
/// найденных серверах и ошибках удалённого. Профиля и агента нет — сообщения подаём сами, в том виде, в каком их шлёт
/// <see cref="ServerMonitor"/>.
/// </summary>
public sealed class ServerViewModelHostTests
{
    private static void WithServer(Action<ServerViewModel, IServerMonitorHost> body) => Sta.Run(() =>
    {
        var server = new ServerViewModel(null!);
        body(server, server);
    });

    private static AgentStatus Running(int serverPid) => new()
    {
        State = ServerState.Running,
        ServerPid = serverPid,
        StartedAt = DateTime.Now.AddMinutes(-5),
        MemoryMb = 1024,
        AgentPid = 1,
        Players = [new OnlinePlayer("Erney", DateTime.Now.AddMinutes(-1), null)],
    };

    [Fact]
    public void Status_FillsTheTiles_AndDetach_ClearsThem() => WithServer((server, host) =>
    {
        host.OnStatus(Running(42));

        Assert.True(server.AgentRunning);
        Assert.Equal(ServerState.Running, server.State);
        Assert.Equal("42", server.PidText);
        Assert.Equal("1", server.PlayersText);
        Assert.True(server.CanCommand);

        host.OnDetached();

        Assert.False(server.AgentRunning);
        Assert.Equal(ServerState.Stopped, server.State);
        Assert.Equal("—", server.PidText);
        Assert.Equal("—", server.UptimeText);
        Assert.Equal("—", server.PlayersText);
        Assert.False(server.CanCommand);
    });

    // «наш» решается до поиска: сервер работал под агентом — его PID не чужой; не работал — любой найденный чужой
    [Fact]
    public void LocalCheck_OurServerPid_IsNotForeign() => WithServer((server, host) =>
    {
        host.OnStatus(Running(42));

        host.OnLocalChecked([42, 77], ourServerUp: true);
        Assert.Equal(77, server.ForeignPid);

        host.OnLocalChecked([42], ourServerUp: true);
        Assert.Null(server.ForeignPid);
        Assert.False(server.HasForeign);

        host.OnLocalChecked([42], ourServerUp: false);
        Assert.Equal(42, server.ForeignPid);
        Assert.True(server.HasForeign);
    });

    [Fact]
    public void RemoteCheck_ErrorShowsWhatHappened_AndTheNextGoodPassClearsIt() => WithServer((server, host) =>
    {
        var error = new HttpRequestException("connection refused");

        host.OnRemoteChecked(error);
        Assert.Equal(RemoteSecret.Describe(error), server.ErrorText);

        host.OnRemoteAnswered();
        host.OnRemoteChecked(null);
        Assert.Equal("", server.ErrorText);
    });

    // пока идёт действие с кнопки, её ошибку удачный проход не стирает
    [Fact]
    public void RemoteCheck_GoodPassWhileBusy_KeepsTheButtonError() => WithServer((server, host) =>
    {
        server.ErrorText = "button failed";
        server.IsBusy = true;

        host.OnRemoteAnswered();
        host.OnRemoteChecked(null);

        Assert.Equal("button failed", server.ErrorText);
    });

    // агент обновляется по нашей просьбе и перезапускается — пропажа связи не ошибка, а этап обновления
    [Fact]
    public void RemoteCheck_ErrorWhileAgentRestarts_IsNotAnError() => WithServer((server, host) =>
    {
        host.OnStatus(new AgentStatus { State = ServerState.Stopped, SelfUpdate = "restart", AgentPid = 1 });

        host.OnRemoteChecked(new HttpRequestException("gone"));

        Assert.Equal("", server.ErrorText);
        Assert.Equal(Loc.T("selfupd.stateRestart"), server.AgentNote);
    });

    [Fact]
    public void RemoteCodeUnreadable_AsksToPasteTheCodeAgain() => WithServer((server, host) =>
    {
        host.OnRemoteCodeUnreadable();

        Assert.Equal(Loc.T("remote.errDecrypt"), server.ErrorText);
    });
}

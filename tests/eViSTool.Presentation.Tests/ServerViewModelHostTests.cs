using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Раздел «Сервер» как хозяин наблюдателя: что он показывает, когда наблюдатель сообщает о статусе, пропаже связи,
/// найденных серверах и ошибках удалённого: плитки, заглушка пустой консоли, «дышащая» точка. Профиля и агента нет —
/// сообщения подаём сами, в том виде, в каком их шлёт <see cref="ServerMonitor"/>.
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
        // сервера нет — значений нет: плитки пустые (вид их приглушает), без прочерков
        Assert.Equal("", server.PidText);
        Assert.Equal("", server.UptimeText);
        Assert.Equal("", server.MemoryText);
        Assert.Equal("", server.PlayersText);
        Assert.False(server.CanCommand);
    });

    private static ConsoleLineViewModel Line(string text) => new(new ConsoleLine(1, DateTime.Now, ConsoleLineKind.Output, text));

    // заглушка «Сервер остановлен» вместо консоли — только у остановленного сервера и пока строк нет
    [Fact]
    public void ConsoleEmpty_OnlyForAStoppedServerWithoutLines() => WithServer((server, host) =>
    {
        server.IsServerProfile = true;
        var changed = new List<string?>();
        server.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        // первый проход слежения ещё не кончился: остановлен ли сервер, неизвестно — заглушки нет
        Assert.False(server.ShowConsoleEmpty);
        host.OnLocalChecked([], ourServerUp: false);
        Assert.True(server.ShowConsoleEmpty);
        Assert.Contains(nameof(ServerViewModel.ShowConsoleEmpty), changed);
        Assert.Equal(Loc.T("server.emptyStopped"), server.ConsoleEmptyTitle);
        Assert.True(server.ShowConsoleEmptyStart);
        Assert.True(server.CanStart); // кнопка в заглушке — та же «Запустить», что сверху

        changed.Clear();
        server.Lines.Add(Line("Server stopped earlier"));
        Assert.False(server.ShowConsoleEmpty); // строки есть — обычная консоль, даже у остановленного
        Assert.Contains(nameof(ServerViewModel.ShowConsoleEmpty), changed);
        server.Lines.Clear();
        Assert.True(server.ShowConsoleEmpty);

        host.OnStatus(new AgentStatus { State = ServerState.Starting, AgentPid = 1 });
        Assert.False(server.ShowConsoleEmpty); // запускается — консоль, строки вот-вот пойдут
        host.OnStatus(Running(42));
        Assert.False(server.ShowConsoleEmpty);
        host.OnDetached();
        Assert.True(server.ShowConsoleEmpty);

        host.OnLocalChecked([77], ourServerUp: false);
        Assert.False(server.ShowConsoleEmpty); // работает чужой сервер — о нём плашка, «остановлен» было бы неправдой

        host.OnLocalChecked([], ourServerUp: false);
        server.IsServerProfile = false;
        Assert.False(server.ShowConsoleEmpty);
    });

    // запустить нельзя — кнопки нет, вместо неё причина; мешает только идущее действие — кнопка остаётся (неактивной)
    [Fact]
    public void ConsoleEmpty_WithoutAWayToStart_SaysWhy() => WithServer((server, host) =>
    {
        server.IsServerProfile = true;
        server.IsRemoteProfile = true;
        Assert.False(server.ShowConsoleEmpty); // подключиться ещё не пробовали — «нет связи» говорить рано
        host.OnRemoteChecked(new HttpRequestException("connection refused"));
        Assert.True(server.ShowConsoleEmpty);
        Assert.Equal(Loc.T("server.emptyOffline"), server.ConsoleEmptyTitle); // остановлен ли — неизвестно
        Assert.Equal(Loc.T("server.emptyOfflineHint"), server.ConsoleEmptyNote);
        Assert.False(server.ShowConsoleEmptyStart);

        server.AgentRunning = true; // связь вернулась: сервер там остановлен (статус удалённого шёл бы ещё и в «Мои моды»)
        Assert.Equal(Loc.T("server.emptyStopped"), server.ConsoleEmptyTitle);
        Assert.Equal("", server.ConsoleEmptyNote);
        Assert.True(server.ShowConsoleEmptyStart);

        server.IsBusy = true;
        Assert.True(server.ShowConsoleEmptyStart);
        Assert.False(server.CanStart);
        server.IsBusy = false;

        var changed = new List<string?>();
        server.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        server.Schedule.IsRestoring = true; // мир восстанавливается из копии
        Assert.Equal(Loc.T("server.emptyRestoring"), server.ConsoleEmptyNote);
        Assert.False(server.ShowConsoleEmptyStart);
        Assert.Contains(nameof(ServerViewModel.ShowConsoleEmptyStart), changed);
    });

    // «дышащая» точка — только у работающего: не у запускающегося, не у остановленного, не без агента
    [Fact]
    public void IsRunning_OnlyWhileTheServerRuns() => WithServer((server, host) =>
    {
        server.IsServerProfile = true;
        var changed = new List<string?>();
        server.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.False(server.IsRunning);

        host.OnStatus(new AgentStatus { State = ServerState.Starting, AgentPid = 1 });
        Assert.False(server.IsRunning);
        host.OnStatus(Running(42));
        Assert.True(server.IsRunning);
        Assert.Contains(nameof(ServerViewModel.IsRunning), changed);
        host.OnStatus(new AgentStatus { State = ServerState.Stopping, AgentPid = 1 });
        Assert.False(server.IsRunning);
        host.OnStatus(Running(42));
        host.OnDetached();
        Assert.False(server.IsRunning);
    });

    // у хостера агента нет: работает ли сервер — по свежему опросу его лога (сам опрос — HostedServerViewModelTests)
    [Fact]
    public void IsRunning_OfAHostedServer_FollowsItsLog() => WithServer((server, _) =>
    {
        server.IsHostedProfile = true;
        var changed = new List<string?>();
        server.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.False(server.IsRunning);

        server.Hosted.IsRunning = true;
        Assert.True(server.IsRunning);
        Assert.Contains(nameof(ServerViewModel.IsRunning), changed);

        // состояние, прочитанное самим разделом, точку не зажигает: опрос мог устареть
        server.Hosted.IsRunning = false;
        server.Hosted.State = HostedServerState.Running;
        Assert.False(server.IsRunning);

        server.Hosted.IsRunning = true;
        server.IsHostedProfile = false; // сменили на профиль без сервера у хостера — его лог уже ни при чём
        Assert.False(server.IsRunning);
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

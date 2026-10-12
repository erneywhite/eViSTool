using System.Net.Http;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Окно кода подключения: разбор кода, проверка связи и отказ от ответа устаревшей проверки. Коды выдуманные (адреса
/// из TEST-NET), агент подменён делегатом — сети тесты не трогают.
/// </summary>
public sealed class ConnectCodeViewModelTests
{
    private static readonly ConnectionCode Sample = new("192.0.2.10", 42420, "made-up-key-for-tests", new string('a', 64));
    private static readonly ConnectionCode Other = new("192.0.2.77", 42421, "another-made-up-key", new string('b', 64));

    private static readonly AgentStatus Running = new() { State = ServerState.Running, GameVersion = "1.21.5" };

    /// <summary>Модель с подменённой проверкой связи; без подмены проверка в тесте не ожидается.</summary>
    private static ConnectCodeViewModel Model(Func<ConnectionCode, CancellationToken, Task<AgentStatus>>? status = null) =>
        new("Подключить", status ?? ((_, _) => throw new Xunit.Sdk.XunitException("проверка связи в этом тесте не ожидалась")));

    [Fact]
    public void GoodCode_ShowsWhereItLeads_AndAllowsOk()
    {
        var vm = Model();
        Assert.Equal("Подключить", vm.OkText);
        Assert.False(vm.CanAccept);
        Assert.False(vm.CheckCommand.CanExecute(null));

        vm.Text = "  " + Sample + "\r\n"; // из буфера код приходит с пробелами и переводом строки
        Assert.Equal(Sample, vm.Code);
        Assert.Equal(Loc.T("connect.target", Sample.Host, Sample.Port), vm.ParsedText);
        Assert.Equal(RowTone.Muted, vm.ParsedTone);
        Assert.True(vm.CanAccept);
        Assert.True(vm.CheckCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("это не код")]
    [InlineData("evis1.@@@")]
    [InlineData("evis1.eyJoIjoiMTkyLjE2OC4xLjQyIiwicCI6NDI0MjAsImsiOiJReDc")] // обрезан при копировании
    public void BadCode_IsAnError_AndBlocksOkAndCheck(string text)
    {
        var vm = Model();
        vm.Text = text;
        Assert.Null(vm.Code);
        Assert.Equal(Loc.T("connect.bad"), vm.ParsedText);
        Assert.Equal(RowTone.Danger, vm.ParsedTone);
        Assert.False(vm.CanAccept);
        Assert.False(vm.CheckCommand.CanExecute(null));
    }

    [Fact]
    public void CodeWithBrokenFingerprint_IsAnError()
    {
        var vm = Model();
        vm.Text = (Sample with { Fingerprint = "abc" }).ToString();
        Assert.Null(vm.Code);
        Assert.Equal(RowTone.Danger, vm.ParsedTone);
    }

    [Fact]
    public void ClearedField_ShowsNothing()
    {
        var vm = Model();
        vm.Text = "мусор";
        vm.Text = "   ";
        Assert.Null(vm.Code);
        Assert.Equal("", vm.ParsedText);
        Assert.Equal(RowTone.Muted, vm.ParsedTone);
        Assert.False(vm.CanAccept);
    }

    [Theory]
    [InlineData(ServerState.Running, "server.stateRunning")]
    [InlineData(ServerState.Starting, "server.stateStarting")]
    [InlineData(ServerState.Stopping, "server.stateStopping")]
    [InlineData(ServerState.Stopped, "server.stateStopped")]
    public async Task Check_Success_ShowsServerStateAndVersion(ServerState state, string stateKey)
    {
        ConnectionCode? asked = null;
        var vm = Model((code, _) =>
        {
            asked = code;
            return Task.FromResult(new AgentStatus { State = state, GameVersion = "1.21.5" });
        });
        vm.Text = Sample.ToString();

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(Sample, asked);
        Assert.Equal(Loc.T("connect.ok", Loc.T(stateKey), "1.21.5"), vm.CheckText);
        Assert.Equal(RowTone.Good, vm.CheckTone);
        Assert.False(vm.IsChecking);
        Assert.True(vm.CheckCommand.CanExecute(null)); // можно проверить ещё раз
    }

    [Fact]
    public async Task Check_UnknownGameVersion_ShowsQuestionMark()
    {
        var vm = Model((_, _) => Task.FromResult(new AgentStatus { State = ServerState.Stopped }));
        vm.Text = Sample.ToString();
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("connect.ok", Loc.T("server.stateStopped"), "?"), vm.CheckText);
    }

    [Fact]
    public async Task Check_Failure_IsDescribedLikeOtherRemoteErrors()
    {
        Exception[] errors =
        [
            new InvalidOperationException("401 Unauthorized"),             // ключ не подходит
            new HttpRequestException("No connection could be made"),     // агент не отвечает
            new TaskCanceledException("timeout"),                        // не дождались ответа (не отмена)
        ];
        foreach (var error in errors)
        {
            var vm = Model((_, _) => Task.FromException<AgentStatus>(error));
            vm.Text = Sample.ToString();

            await vm.CheckCommand.ExecuteAsync(null);

            Assert.Equal(RemoteSecret.Describe(error), vm.CheckText);
            Assert.Equal(RowTone.Danger, vm.CheckTone);
            Assert.False(vm.IsChecking);
            Assert.True(vm.CheckCommand.CanExecute(null));
        }
        Assert.Equal(Loc.T("remote.errKey"), RemoteSecret.Describe(errors[0]));
    }

    [Fact]
    public async Task WhileChecking_ShowsProgress_AndCheckWaits()
    {
        var answer = new TaskCompletionSource<AgentStatus>();
        var vm = Model((_, _) => answer.Task);
        vm.Text = Sample.ToString();

        var run = vm.CheckCommand.ExecuteAsync(null);

        Assert.True(vm.IsChecking);
        Assert.Equal(Loc.T("connect.checking"), vm.CheckText);
        Assert.Equal(RowTone.Muted, vm.CheckTone);
        Assert.False(vm.CheckCommand.CanExecute(null));
        Assert.True(vm.CanAccept); // «ОК» проверки не ждёт

        answer.SetResult(Running);
        await run;
        Assert.Equal(RowTone.Good, vm.CheckTone);
    }

    // Запрос не слушает отмену (так и задумано): ответ всё равно приходит, но уже не про тот код, что в поле
    [Fact]
    public async Task CodeChangedDuringCheck_StaleAnswerIsDropped()
    {
        var answer = new TaskCompletionSource<AgentStatus>();
        var token = CancellationToken.None;
        var vm = Model((_, ct) =>
        {
            token = ct;
            return answer.Task;
        });
        vm.Text = Sample.ToString();
        var run = vm.CheckCommand.ExecuteAsync(null);

        vm.Text = Other.ToString();

        Assert.True(token.IsCancellationRequested);
        Assert.False(vm.IsChecking);
        Assert.Equal("", vm.CheckText);
        Assert.True(vm.CheckCommand.CanExecute(null)); // новый код можно проверять, не дожидаясь старого ответа

        answer.SetResult(Running);
        await run;
        Assert.Equal("", vm.CheckText);
        Assert.False(vm.IsChecking);
        Assert.Equal(Other, vm.Code);
    }

    // Запрос слушает отмену: отменённый запрос не превращается в «нет связи», а новая проверка идёт своим чередом
    [Fact]
    public async Task CodeChangedDuringCheck_CancelledRequestShowsNoError_AndNewCheckWorks()
    {
        var calls = new List<ConnectionCode>();
        var vm = Model(async (code, ct) =>
        {
            calls.Add(code);
            if (code == Sample) await Task.Delay(Timeout.Infinite, ct);
            return Running;
        });
        vm.Text = Sample.ToString();
        var stale = vm.CheckCommand.ExecuteAsync(null);

        vm.Text = Other.ToString();
        await stale;
        Assert.Equal("", vm.CheckText);

        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(new[] { Sample, Other }, calls);
        Assert.Equal(Loc.T("connect.ok", Loc.T("server.stateRunning"), "1.21.5"), vm.CheckText);
        Assert.Equal(RowTone.Good, vm.CheckTone);
    }

    // Окно закрыли, пока шла проверка: запрос отменяется, ответ не показывается
    [Fact]
    public async Task CancelCheck_DropsRunningCheck()
    {
        var answer = new TaskCompletionSource<AgentStatus>();
        var token = CancellationToken.None;
        var vm = Model((_, ct) =>
        {
            token = ct;
            return answer.Task;
        });
        vm.Text = Sample.ToString();
        var run = vm.CheckCommand.ExecuteAsync(null);

        vm.CancelCheck();
        Assert.True(token.IsCancellationRequested);
        Assert.False(vm.IsChecking);
        Assert.Equal("", vm.CheckText);

        answer.SetResult(Running);
        await run;
        Assert.Equal("", vm.CheckText); // ответ не записан поверх
        Assert.Equal(Sample, vm.Code);
    }
}

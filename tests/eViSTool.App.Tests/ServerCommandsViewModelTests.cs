using System.ComponentModel;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.App.Tests;

/// <summary>
/// Окно «Команды сервера»: поиск по всем словам, порядок по имени, вставка (выбранная, первая подходящая, заданная
/// двойным щелчком), обновление списка на лету без потери выбранной строки, кнопка «Обновить» только у работающего
/// сервера и отписка от сервера при закрытии.
/// </summary>
public sealed class ServerCommandsViewModelTests
{
    private static readonly ServerCommand Tp = new("tp", "<source> <target>", "Teleport a player to another player");
    private static readonly ServerCommand Time = new("time", "set|add <value>", "Set the world time");
    private static readonly ServerCommand Kick = new("kick", "<player> [reason]", "Kick a player from the server");
    private static readonly ServerCommand Carry = new("CarryOn", "reload", "Reload the Carry On configuration");
    private static readonly ServerCommand Stats = new("stats", "", "Show server statistics");

    /// <summary>Раздел «Сервер» без главной модели и без агента: список команд и «работает ли сервер» задаём сами.</summary>
    private static void With(Action<ServerViewModel, ServerCommandsViewModel, List<ServerCommand>> body, params ServerCommand[] commands) => Sta.Run(() =>
    {
        var server = new ServerViewModel(null!);
        server.SetCommands(commands);
        using var model = new ServerCommandsViewModel(server);
        var accepted = new List<ServerCommand>();
        model.Accepted += accepted.Add;
        body(server, model, accepted);
    });

    private static IEnumerable<string> Names(ServerCommandsViewModel model) => model.View.Items.Select(c => c.Name);

    // ---- список

    [Fact]
    public void Lists_AllCommands_ByNameIgnoringCase() => With((_, model, _) =>
    {
        Assert.Equal(["CarryOn", "kick", "stats", "time", "tp"], Names(model));
        Assert.Equal(Loc.T("cmds.count", 5, 5), model.Status);
        Assert.Null(model.Selected);
    }, Tp, Time, Kick, Carry, Stats);

    [Fact]
    public void NoCommands_SaysTheListIsUnknown_AndInsertStaysSilent() => With((_, model, accepted) =>
    {
        Assert.Empty(model.View.Items);
        Assert.Equal(Loc.T("cmds.empty"), model.Status);

        model.InsertCommand.Execute(null);

        Assert.Empty(accepted);
    });

    [Fact]
    public void ServerCommands_ShownFromTheStart_NotOnlyAfterTheFirstUpdate() => With((_, model, _) =>
    {
        // окно открывают, когда список уже есть (кнопка «Команды» в консоли)
        Assert.Equal(["time", "tp"], Names(model));
    }, Tp, Time);

    // ---- поиск

    [Fact]
    public void Search_NeedsEveryWord_InUsageOrDescription() => With((_, model, _) =>
    {
        model.Search = "player";
        Assert.Equal(["kick", "tp"], Names(model)); // и в описании, и в аргументах

        model.Search = "player server";
        Assert.Equal(["kick"], Names(model)); // слова из разных мест, но все

        model.Search = "teleport server";
        Assert.Empty(model.View.Items); // второго слова нет нигде
    }, Tp, Time, Kick);

    [Fact]
    public void Search_IgnoresCase_LeadingSlashAndExtraSpaces() => With((_, model, _) =>
    {
        model.Search = "  /TP  ";
        Assert.Equal(["tp"], Names(model));

        model.Search = "/ source   TARGET";
        Assert.Equal(["tp"], Names(model));

        model.Search = "carry on";
        Assert.Equal(["CarryOn"], Names(model)); // слова ищутся порознь: «carry» и «on» есть и в имени, и в описании
    }, Tp, Time, Carry);

    [Fact]
    public void Search_LooksAtArguments() => With((_, model, _) =>
    {
        model.Search = "<value>";
        Assert.Equal(["time"], Names(model));
    }, Tp, Time);

    [Fact]
    public void Search_EmptyOrOnlySlash_ShowsEverything() => With((_, model, _) =>
    {
        model.Search = "tp";
        Assert.Single(model.View.Items);

        model.Search = "/";
        Assert.Equal(2, model.View.Count);

        model.Search = "";
        Assert.Equal(2, model.View.Count);
    }, Tp, Time);

    [Fact]
    public void Search_NullFromTheField_ShowsEverything() => With((_, model, _) =>
    {
        model.Search = "tp";
        model.Search = null!; // пустое поле у Avalonia бывает null

        Assert.Equal(2, model.View.Count);
    }, Tp, Time);

    [Fact]
    public void Status_CountsFoundOfAll_AndIsRaisedBySearch() => With((_, model, _) =>
    {
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.Search = "time";

        Assert.Equal(Loc.T("cmds.count", 1, 3), model.Status);
        Assert.Contains(nameof(ServerCommandsViewModel.Status), raised);

        model.Search = "nothing like it";
        Assert.Equal(Loc.T("cmds.count", 0, 3), model.Status);
    }, Tp, Time, Kick);

    // ---- вставка

    [Fact]
    public void Insert_WithoutSelection_TakesTheFirstFound() => With((_, model, accepted) =>
    {
        model.InsertCommand.Execute(null);
        Assert.Same(Kick, Assert.Single(accepted)); // «kick» первая по алфавиту

        accepted.Clear();
        model.Search = "time";
        model.InsertCommand.Execute(null);
        Assert.Same(Time, Assert.Single(accepted));
    }, Tp, Time, Kick);

    [Fact]
    public void Insert_TakesTheSelected_NotTheFirst() => With((_, model, accepted) =>
    {
        model.Selected = Tp;

        model.InsertCommand.Execute(null);

        Assert.Same(Tp, Assert.Single(accepted));
    }, Tp, Time, Kick);

    // двойной щелчок передаёт строку, по которой щёлкнули
    [Fact]
    public void Insert_WithRowFromDoubleClick_TakesThatRow() => With((_, model, accepted) =>
    {
        model.Selected = Tp;

        model.InsertCommand.Execute(Time);

        Assert.Same(Time, Assert.Single(accepted));
    }, Tp, Time, Kick);

    [Fact]
    public void Insert_NothingFound_StaysOpen() => With((_, model, accepted) =>
    {
        model.Search = "nothing like it";

        model.InsertCommand.Execute(null);

        Assert.Empty(accepted);
    }, Tp, Time);

    // ---- выбор при поиске

    [Fact]
    public void Selection_StaysWhileItStillMatches_AndDropsWhenHidden() => With((_, model, _) =>
    {
        model.Selected = Time;

        model.Search = "ti";
        Assert.Same(Time, model.Selected);

        model.Search = "tp";
        Assert.Null(model.Selected); // строки уже нет в списке — «Вставить» берёт первую подходящую, а не невидимую
    }, Tp, Time, Kick);

    [Fact]
    public void Insert_AfterTheSelectedRowWasFilteredOut_TakesTheFirstFound() => With((_, model, accepted) =>
    {
        model.Selected = Kick;
        model.Search = "tp";

        model.InsertCommand.Execute(null);

        Assert.Same(Tp, Assert.Single(accepted));
    }, Tp, Time, Kick);

    // ---- список меняется на лету

    [Fact]
    public void ServerAnswersHelp_ListUpdates_AndTheSameRowsKeepTheirPlace() => With((server, model, _) =>
    {
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var kickRow = model.View.Items.Single(c => c.Name == "kick");
        model.Selected = kickRow;

        // тот же набор плюс новая команда мода, объекты — новые (сервер ответил заново), но равные по значению
        server.SetCommands([new(Tp.Name, Tp.Args, Tp.Description), new(Kick.Name, Kick.Args, Kick.Description), Carry]);

        Assert.Equal(["CarryOn", "kick", "tp"], Names(model));
        Assert.Same(kickRow, model.View.Items.Single(c => c.Name == "kick")); // тот же объект — выбор и прокрутка не сбились
        Assert.Same(kickRow, model.Selected);
        Assert.Equal(Loc.T("cmds.count", 3, 3), model.Status);
        Assert.Contains(nameof(ServerCommandsViewModel.Status), raised);
    }, Tp, Kick);

    [Fact]
    public void ServerListUpdate_RespectsTheSearch_AndDropsTheRemovedSelection() => With((server, model, _) =>
    {
        model.Search = "player";
        model.Selected = Kick;
        Assert.Equal(["kick", "tp"], Names(model));

        server.SetCommands([Tp, Time, new("ban", "<player>", "Ban a player")]);

        Assert.Equal(["ban", "tp"], Names(model)); // «kick» пропала с сервера, «ban» подошла под поиск
        Assert.Null(model.Selected);
        Assert.Equal(Loc.T("cmds.count", 2, 3), model.Status);
    }, Tp, Time, Kick);

    [Fact]
    public void ServerForgetsTheList_WindowShowsTheEmptyState() => With((server, model, _) =>
    {
        // профиль переключили или агент перезапустился — сервер сбросил список
        server.SetCommands([]);

        Assert.Empty(model.View.Items);
        Assert.Equal(Loc.T("cmds.empty"), model.Status);
    }, Tp, Time);

    [Fact]
    public void ServerListWithRepeats_ShowsEachCommandOnce() => With((_, model, _) =>
    {
        Assert.Equal(["tp"], Names(model));
        Assert.Equal(Loc.T("cmds.count", 1, 1), model.Status);
    }, Tp, new ServerCommand(Tp.Name, Tp.Args, Tp.Description));

    // ---- «Обновить список»

    [Fact]
    public void Refresh_OnlyWhileTheServerIsRunning_AndFollowsItsState() => Sta.Run(() =>
    {
        var server = new ServerViewModel(null!);
        var asked = 0;
        using var model = new ServerCommandsViewModel(server, () => asked++);
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var canExecuteChanged = 0;
        model.RefreshCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;

        // сервер стоит — спрашивать некого
        Assert.False(model.CanRefresh);
        Assert.False(model.RefreshCommand.CanExecute(null));

        server.AgentRunning = true;
        server.State = ServerState.Running;

        Assert.True(model.CanRefresh);
        Assert.True(model.RefreshCommand.CanExecute(null));
        Assert.Contains(nameof(ServerCommandsViewModel.CanRefresh), raised);
        Assert.True(canExecuteChanged > 0);

        model.RefreshCommand.Execute(null);
        Assert.Equal(1, asked);

        server.State = ServerState.Starting; // запускается — спрашивать уже можно
        Assert.True(model.CanRefresh);

        server.State = ServerState.Stopped; // остановили — кнопка снова выключена
        Assert.False(model.CanRefresh);
        Assert.False(model.RefreshCommand.CanExecute(null));
        Assert.Equal(1, asked);
    });

    [Fact]
    public void Refresh_WithoutOurServerOnline_DoesNothingAndDoesNotFail() => Sta.Run(() =>
    {
        // обычный путь: у раздела «Сервер» нет агента — запрос списка тихо ничего не делает
        var server = new ServerViewModel(null!);
        server.AgentRunning = true;
        server.State = ServerState.Running;
        using var model = new ServerCommandsViewModel(server);

        model.RefreshCommand.Execute(null);

        Assert.Empty(model.View.Items);
    });

    // ---- закрытие окна

    [Fact]
    public void Dispose_StopsListeningToTheServer() => Sta.Run(() =>
    {
        var server = new ServerViewModel(null!);
        server.SetCommands([Tp]);
        var model = new ServerCommandsViewModel(server);
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.Dispose();
        model.Dispose(); // повторно — не страшно
        server.SetCommands([Tp, Time]);
        server.AgentRunning = true;
        server.State = ServerState.Running;

        Assert.Equal(["tp"], Names(model)); // новая команда сервера уже не доходит
        Assert.DoesNotContain(nameof(ServerCommandsViewModel.Status), raised);
        Assert.DoesNotContain(nameof(ServerCommandsViewModel.CanRefresh), raised);
    });

    [Fact]
    public void UnrelatedServerChanges_AreIgnored() => With((server, model, _) =>
    {
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        server.CommandText = "/ti"; // подсказки консоли — не наше дело
        server.ErrorText = "что-то случилось";

        Assert.Empty(raised);
        Assert.Equal(2, model.View.Count);
    }, Tp, Time);
}

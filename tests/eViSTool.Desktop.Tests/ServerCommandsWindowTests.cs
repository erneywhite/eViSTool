using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Desktop.Dialogs;
using eViSTool.Desktop.Platform;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Окно «Команды сервера» на настоящей модели: поиск сразу в фокусе, ↓ из поиска — в список, Enter (в поиске и в списке) и
/// двойной щелчок вставляют команду, «Отмена» закрывает без ответа, «Обновить» выключена, пока сервер стоит.
/// </summary>
public sealed class ServerCommandsWindowTests
{
    private static readonly ServerCommand Tp = new("tp", "<source> <target>", "Teleport a player to another player");
    private static readonly ServerCommand Time = new("time", "set|add <value>", "Set the world time");
    private static readonly ServerCommand Kick = new("kick", "<player> [reason]", "Kick a player from the server");

    private static (ServerCommandsWindow Window, ServerCommandsViewModel Model, ServerViewModel Server, List<ServerCommand> Accepted) Open(
        params ServerCommand[] commands)
    {
        if (!Ui.IsSet) Ui.Platform = new AvaloniaUiPlatform(); // конфигу раздела «Сервер» нужен таймер платформы
        var server = new ServerViewModel(null!);
        server.SetCommands(commands);
        var model = new ServerCommandsViewModel(server);
        var accepted = new List<ServerCommand>();
        model.Accepted += accepted.Add;
        var window = new ServerCommandsWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, model, server, accepted);
    }

    private static T Named<T>(Window window, string automationId) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    private static void Press(Window window, Key key, PhysicalKey physical) => window.KeyPress(key, RawInputModifiers.None, physical, null);

    [AvaloniaFact]
    public async Task Opens_WithRowsByName_StatusAndFocusInSearch()
    {
        var (window, model, _, _) = Open(Tp, Time, Kick);
        Assert.Equal(Loc.T("cmds.title"), window.Title);

        var list = Named<ListBox>(window, "CmdsList");
        Assert.Equal(["kick", "time", "tp"], list.Items.Cast<ServerCommand>().Select(c => c.Name));
        Assert.Equal(Loc.T("cmds.count", 3, 3), Named<TextBlock>(window, "CmdsStatus").Text);
        // строка читается целиком: «/tp <source> <target>» и описание
        var first = list.ContainerFromIndex(0)!;
        Assert.Contains(first.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "/kick <player> [reason]");
        Assert.Contains(first.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Kick a player from the server");

        var search = Named<TextBox>(window, "CmdsSearch");
        await Wait.Until(() => search.IsFocused);
        Assert.Null(model.Selected);
        window.Close();
    }

    [AvaloniaFact]
    public void Typing_FiltersTheList_AndTheStatusFollows()
    {
        var (window, _, _, _) = Open(Tp, Time, Kick);
        var list = Named<ListBox>(window, "CmdsList");

        Named<TextBox>(window, "CmdsSearch").Text = "player";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["kick", "tp"], list.Items.Cast<ServerCommand>().Select(c => c.Name));
        Assert.Equal(Loc.T("cmds.count", 2, 3), Named<TextBlock>(window, "CmdsStatus").Text);
        window.Close();
    }

    [AvaloniaFact]
    public void ServerAnswers_ListUpdatesAndSelectionStays()
    {
        var (window, model, server, _) = Open(Tp, Kick);
        var list = Named<ListBox>(window, "CmdsList");
        list.SelectedIndex = 1; // «tp»
        Assert.Same(Tp, model.Selected);

        server.SetCommands([Tp, Kick, Time]);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["kick", "time", "tp"], list.Items.Cast<ServerCommand>().Select(c => c.Name));
        Assert.Same(Tp, list.SelectedItem); // строка осталась выбранной, хотя список пополнился
        Assert.Same(Tp, model.Selected);
        window.Close();
    }

    [AvaloniaFact]
    public void ArrowDown_FromSearch_SelectsFirstRow_AndMovesFocusToTheList()
    {
        var (window, model, _, _) = Open(Tp, Time, Kick);
        var search = Named<TextBox>(window, "CmdsSearch");
        var list = Named<ListBox>(window, "CmdsList");
        search.Focus();

        Press(window, Key.Down, PhysicalKey.ArrowDown);

        Assert.Same(model.View.Items[0], model.Selected);
        Assert.Equal(0, list.SelectedIndex);
        Assert.True(list.ContainerFromIndex(0)!.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void ArrowDown_WhenNothingFound_StaysInSearch()
    {
        var (window, model, _, _) = Open(Tp, Time);
        var search = Named<TextBox>(window, "CmdsSearch");
        search.Text = "nothing like it";
        Dispatcher.UIThread.RunJobs();
        search.Focus();

        Press(window, Key.Down, PhysicalKey.ArrowDown);

        Assert.Null(model.Selected);
        Assert.True(search.IsFocused);
        window.Close();
    }

    // Enter в поле поиска — кнопка по умолчанию «Вставить»: первая подходящая
    [AvaloniaFact]
    public void Enter_InSearch_InsertsTheFirstFound_AndClosesTheWindow()
    {
        var (window, _, _, accepted) = Open(Tp, Time, Kick);
        var search = Named<TextBox>(window, "CmdsSearch");
        search.Text = "t";
        Dispatcher.UIThread.RunJobs();
        search.Focus();

        Press(window, Key.Enter, PhysicalKey.Enter);

        Assert.Same(Kick, Assert.Single(accepted)); // «kick» первая из найденных по «t» («the server»)
        Assert.False(window.IsVisible);
    }

    // Enter в списке забирает сам список — до кнопки по умолчанию он не доходит, окно перехватывает
    [AvaloniaFact]
    public void Enter_InList_InsertsTheSelectedRow()
    {
        var (window, _, _, accepted) = Open(Tp, Time, Kick);
        var list = Named<ListBox>(window, "CmdsList");
        list.SelectedIndex = 2; // «tp»
        list.ContainerFromIndex(2)!.Focus();

        Press(window, Key.Enter, PhysicalKey.Enter);

        Assert.Same(Tp, Assert.Single(accepted));
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void DoubleClick_OnRow_InsertsThatRow()
    {
        var (window, _, _, accepted) = Open(Tp, Time, Kick);
        var row = Named<ListBox>(window, "CmdsList").ContainerFromIndex(1)!; // «time»

        Mouse.Click(window, row, count: 2);

        Assert.Same(Time, Assert.Single(accepted));
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void InsertButton_InsertsTheSelectedRow()
    {
        var (window, model, _, accepted) = Open(Tp, Time);
        model.Selected = Tp;

        Named<Button>(window, "CmdsInsert").Command!.Execute(null);

        Assert.Same(Tp, Assert.Single(accepted));
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void EmptyList_InsertDoesNotCloseTheWindow()
    {
        var (window, _, _, accepted) = Open();
        Assert.Equal(Loc.T("cmds.empty"), Named<TextBlock>(window, "CmdsStatus").Text);

        Named<Button>(window, "CmdsInsert").Command!.Execute(null);
        Press(window, Key.Enter, PhysicalKey.Enter);

        Assert.Empty(accepted);
        Assert.True(window.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Escape_ClosesWithoutAnAnswer()
    {
        var (window, _, _, accepted) = Open(Tp);

        Press(window, Key.Escape, PhysicalKey.Escape);

        Assert.False(window.IsVisible);
        Assert.Empty(accepted);
    }

    [AvaloniaFact]
    public void RefreshButton_FollowsTheServerState()
    {
        var (window, _, server, _) = Open(Tp);
        var refresh = Named<Button>(window, "CmdsRefresh");
        Assert.False(refresh.IsEffectivelyEnabled); // сервер стоит

        server.AgentRunning = true;
        server.State = ServerState.Running;
        Dispatcher.UIThread.RunJobs();
        Assert.True(refresh.IsEffectivelyEnabled);

        server.State = ServerState.Stopped;
        Dispatcher.UIThread.RunJobs();
        Assert.False(refresh.IsEffectivelyEnabled);
        window.Close();
    }
}

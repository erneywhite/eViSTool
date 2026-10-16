using System.ComponentModel;
using System.Reflection;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Консоль раздела «Сервер» сама по себе, без агента: подсказки и подсказка аргументов, Tab, стрелки по подсказкам,
/// отправка без связи, сброс при смене профиля. И что раздел отдаёт виду свойства консоли под прежними именами вместе
/// с уведомлениями: вид и окно «Команды» привязаны к разделу, а не к консоли.
/// </summary>
public sealed class ServerConsoleViewModelTests
{
    private static readonly ServerCommand Tp = new("tp", "<source> <target>", "Teleport a player to another player");
    private static readonly ServerCommand Time = new("time", "set|add <value>", "Set the world time");
    private static readonly ServerCommand Tell = new("tell", "<player> <message>", "Send a private message");

    /// <summary>Консоль без связи с агентом: список команд задаём сами, как снимки окна.</summary>
    private static ServerConsoleViewModel NewConsole(params ServerCommand[] commands)
    {
        var console = new ServerConsoleViewModel(() => null);
        console.SetCommands(commands);
        return console;
    }

    private static string Hint(ServerCommand c) => $"{c.Usage}   —   {c.Description}";

    [Fact]
    public void Typing_SuggestsCommands_AndHintsArgumentsOfTheTypedOne()
    {
        var console = NewConsole(Tp, Time, Tell);

        console.CommandText = "/t";
        Assert.Equal([Tell, Time, Tp], console.Suggestions);
        Assert.True(console.ShowSuggestions);
        Assert.Equal("", console.ArgsHint);

        // набрано имя целиком — список не нужен, хватит подсказки аргументов
        console.CommandText = "/tp";
        Assert.False(console.ShowSuggestions);
        Assert.Equal(Hint(Tp), console.ArgsHint);

        console.CommandText = "/tp Erney ";
        Assert.Empty(console.Suggestions);
        Assert.Equal(Hint(Tp), console.ArgsHint);
    }

    [Fact]
    public void CommandsUnknown_SlashHintsThatTheListIsNotKnownYet()
    {
        var console = NewConsole();

        console.CommandText = "/ti";
        Assert.Equal(Loc.T("cmds.unknownHint"), console.ArgsHint);

        console.CommandText = "ti";
        Assert.Equal("", console.ArgsHint);
    }

    [Fact]
    public void Tab_CompletesTheOnlyMatch_ButNotACommonStartAlreadyTyped()
    {
        var console = NewConsole(Tp, Time, Tell);

        console.CommandText = "/te";
        Assert.True(console.CompleteCommand());
        Assert.Equal("/tell ", console.CommandText);

        console.CommandText = "/t"; // «tell», «time», «tp» — общее начало уже набрано
        Assert.False(console.CompleteCommand());
        Assert.Equal("/t", console.CommandText);
    }

    [Fact]
    public void Arrows_WalkTheSuggestions_FromTheEdge_AndTabTakesTheChosenOne()
    {
        var console = NewConsole(Tp, Time, Tell);
        console.CommandText = "/t";

        console.MoveSuggestion(-1); // ничего не выбрано: ↑ — с последней
        Assert.Same(Tp, console.SelectedSuggestion);
        console.MoveSuggestion(+1); // дальше конца не уходит
        Assert.Same(Tp, console.SelectedSuggestion);
        console.MoveSuggestion(-1);
        console.MoveSuggestion(-1);
        console.MoveSuggestion(-1);
        Assert.Same(Tell, console.SelectedSuggestion);

        Assert.True(console.CompleteCommand());
        Assert.Equal("/tell ", console.CommandText);
    }

    [Fact]
    public void HideSuggestions_And_InsertCommand()
    {
        var console = NewConsole(Tp, Time);
        console.CommandText = "/t";

        console.HideSuggestions();
        Assert.False(console.ShowSuggestions);

        console.InsertCommand(Time);
        Assert.Equal("/time ", console.CommandText);
    }

    // связи с агентом нет — отправлять некуда: набранное остаётся в поле, в историю ничего не попадает
    [Fact]
    public void Send_WithoutConnection_KeepsTheText_AndTheHistoryEmpty()
    {
        var console = NewConsole(Time);
        console.CommandText = "time set day";

        Assert.True(console.SendAsync().IsCompletedSuccessfully);
        Assert.Equal("time set day", console.CommandText);

        console.HistoryStep(-1);
        Assert.Equal("time set day", console.CommandText);
    }

    [Fact]
    public void Reset_ClearsLinesAndCommands_AndTellsAboutTheList()
    {
        var console = NewConsole(Tp, Time);
        console.Lines.Add(new ConsoleLineViewModel(new ConsoleLine(1, DateTime.Now, ConsoleLineKind.Output, "old profile")));
        console.CommandText = "/t";
        var raised = new List<string?>();
        console.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        console.Reset();

        Assert.Empty(console.Lines);
        Assert.Empty(console.Commands);
        Assert.Contains(nameof(ServerConsoleViewModel.Commands), raised);
        // подсказки пересчитаны по пустому списку
        Assert.False(console.ShowSuggestions);
        Assert.Equal(Loc.T("cmds.unknownHint"), console.ArgsHint);
        Assert.Equal("/t", console.CommandText); // набранное не трогаем
    }

    // ---- раздел «Сервер» — лицо консоли для вида

    [Fact]
    public void Server_PassesConsoleProperties_WithTheirNotifications() => Sta.Run(() =>
    {
        var server = new ServerViewModel(null!);
        server.SetCommands([Tp, Time]);
        var changing = new List<string?>();
        var changed = new List<string?>();
        ((INotifyPropertyChanging)server).PropertyChanging += (_, e) => changing.Add(e.PropertyName);
        server.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        server.CommandText = "/t";

        Assert.Equal("/t", server.CommandText);
        Assert.Equal([Time, Tp], server.Suggestions);
        Assert.True(server.ShowSuggestions);
        Assert.Contains(nameof(ServerViewModel.CommandText), changing);
        Assert.Contains(nameof(ServerViewModel.CommandText), changed);
        Assert.Contains(nameof(ServerViewModel.Suggestions), changed);
        Assert.Contains(nameof(ServerViewModel.ShowSuggestions), changed);

        // выбор в списке подсказок — двусторонняя привязка
        changed.Clear();
        server.SelectedSuggestion = Tp;
        Assert.Contains(nameof(ServerViewModel.SelectedSuggestion), changed);
        Assert.True(server.CompleteCommand());
        Assert.Equal("/tp ", server.CommandText);
        Assert.Equal(Hint(Tp), server.ArgsHint);
        Assert.Contains(nameof(ServerViewModel.ArgsHint), changed);
    });

    // раздел пересылает виду все уведомления консоли подряд, без фильтра по имени: поэтому у каждого свойства консоли
    // должен быть двойник у раздела, с тем же именем и типом, иначе раздел сообщит об изменении свойства, которого у него нет
    [Fact]
    public void Server_HasATwinForEveryConsoleProperty()
    {
        var properties = typeof(ServerConsoleViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.NotEmpty(properties);
        foreach (var property in properties)
        {
            var twin = typeof(ServerViewModel).GetProperty(property.Name, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(twin is not null, $"у раздела нет свойства консоли {property.Name}");
            Assert.Equal(property.PropertyType, twin.PropertyType);
        }
    }

    [Fact]
    public void Server_CommandsList_ComesFromTheConsole_WithItsNotification() => Sta.Run(() =>
    {
        var server = new ServerViewModel(null!);
        var changed = new List<string?>();
        server.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        server.SetCommands([Tell]);

        Assert.Equal([Tell], server.Commands);
        Assert.Contains(nameof(ServerViewModel.Commands), changed);

        server.InsertCommand(Tell);
        Assert.Equal("/tell ", server.CommandText);
        server.HideSuggestions();
        Assert.False(server.ShowSuggestions);
    });
}

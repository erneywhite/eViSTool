using eViSTool.Core.Server;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Окно «Команды сервера»: dialog.servercommands (список команд — выдуманный набор из /help сервера и модов) и
/// dialog.servercommands.empty (список ещё неизвестен). Сервер стенда не запущен, настоящего списка у него нет, поэтому
/// команды подставляются прямо в модель раздела «Сервер»; на диск они не пишутся. Кнопка «Обновить список» на снимке
/// выключена, как у остановленного сервера.
/// </summary>
internal static class ServerCommandsDialog
{
    [SnapshotDialog("servercommands")]
    private static void Open(MainViewModel main)
    {
        main.Server.SetCommands(Sample());
        AppWindows.Current.PickServerCommand(main.Server);
    }

    /// <summary>Сервер ещё не отвечал на /help: списка нет, внизу подсказка запустить сервер.</summary>
    [SnapshotDialog("servercommands.empty")]
    private static void OpenEmpty(MainViewModel main)
    {
        main.Server.SetCommands([]);
        AppWindows.Current.PickServerCommand(main.Server);
    }

    private static List<ServerCommand> Sample() =>
    [
        new("tp", "<source> <target>", "Teleport a player to another player or to coordinates"),
        new("time", "set|add|stop|resume <value>", "Set, add or stop the world time"),
        new("gamemode", "[player] <mode>", "Change the game mode of a player"),
        new("kick", "<player> [reason]", "Kick a player from the server"),
        new("op", "<player>", "Give a player the admin privilege"),
        new("whitelist", "add|remove|on|off|list [player]", "Manage the server whitelist"),
        new("weather", "set|setir|acp <value>", "Change the current weather"),
        new("worldconfig", "<setting> [value]", "Read or change a world setting"),
        new("stats", "", "Show server performance statistics"),
        new("giveitem", "<code> [amount]", "Give yourself an item by its code"),
        new("carryon", "reload", "Reload the Carry On configuration"),
        new("autosavenow", "", "Save the world right now"),
        new("announce", "<text>", "Show a message to all players on the server"),
    ];
}

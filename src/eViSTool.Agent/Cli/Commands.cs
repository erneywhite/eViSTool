using eViSTool.Core.Localization;

/// <summary>
/// Команды агента для сервера без окна (Linux): первое слово командной строки. Без команды агент просто работает —
/// держит сервер, как и с окном. Каждая группа команд — в своём файле (setup, remote, status/start/stop/restart/command,
/// service). Код выхода: 0 — получилось, 1 — не получилось (причина — в stderr), 2 — неверные слова или ключи.
/// </summary>
internal static class Commands
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int BadUsage = 2;

    /// <summary>Выполнить команду; null — команды нет, агент запускается как обычно.</summary>
    public static int? Run(AgentArgs cli, string[] args)
    {
        if (cli.Command is null) return cli.Help ? Usage(Ok) : null;
        var rest = args.Skip(1).ToArray();
        return cli.Command switch
        {
            "help" => Usage(Ok),
            "setup" => SetupCommand.Run(cli, rest),
            "remote" => RemoteCommand.Run(cli, rest),
            "status" or "start" or "stop" or "restart" or "command" => ControlCommand.Run(cli.Command, cli, rest),
            "service" => ServiceCommand.Run(cli, rest),
            _ => Unknown(cli.Command),
        };
    }

    public static int Usage(int code)
    {
        (code == Ok ? Console.Out : Console.Error).WriteLine(Loc.T("agent.usage"));
        return code;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine(Loc.T("agent.unknownCommand", command));
        return Usage(BadUsage);
    }
}

/// <summary>Команда «setup»: найти сервер, запомнить его папки (data/agent.json), проверить права.</summary>
internal static class SetupCommand
{
    public static int Run(AgentArgs cli, string[] args)
    {
        Console.Error.WriteLine("setup: not implemented yet");
        return Commands.Failed;
    }
}

/// <summary>Команда «remote»: удалённый доступ для окна на Windows — enable, disable, code, host, new-key, status.</summary>
internal static class RemoteCommand
{
    public static int Run(AgentArgs cli, string[] args)
    {
        Console.Error.WriteLine("remote: not implemented yet");
        return Commands.Failed;
    }
}

/// <summary>Команда «service»: служба systemd — install, uninstall, status (Linux, от root).</summary>
internal static class ServiceCommand
{
    public static int Run(AgentArgs cli, string[] args)
    {
        Console.Error.WriteLine("service: not implemented yet");
        return Commands.Failed;
    }
}

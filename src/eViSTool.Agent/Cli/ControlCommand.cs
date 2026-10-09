/// <summary>
/// Команды «status», «start», «stop», «restart», «command»: работают с уже запущенным агентом этого профиля через его
/// локальный HTTP API (адрес и ключ — в data/agents/&lt;профиль&gt;.json и .key), как окно на этом компьютере.
/// </summary>
internal static class ControlCommand
{
    public static int Run(string command, AgentArgs cli, string[] args)
    {
        Console.Error.WriteLine($"{command}: not implemented yet");
        return Commands.Failed;
    }
}

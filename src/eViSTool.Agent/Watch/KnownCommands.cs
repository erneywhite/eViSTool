using eViSTool.Core.Server;

/// <summary>
/// Команды сервера — из его ответа на /help (с командами модов), для подсказок в консоли окна. Помним между запусками,
/// чтобы подсказки были сразу.
/// </summary>
internal sealed class KnownCommands
{
    private readonly string _file;
    private readonly Dictionary<string, ServerCommand> _commands;
    private bool _dirty;

    public KnownCommands(AgentOptions opts)
    {
        _file = ServerCommands.FileFor(opts.ProfileId, opts.AgentsDir);
        _commands = ServerCommands.Load(_file).ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    public int Count => _commands.Count;

    /// <summary>Строка консоли: строка ответа на /help — новая или изменившаяся команда.</summary>
    public void Collect(ConsoleLine line)
    {
        if (line.Kind == ConsoleLineKind.Output && ServerCommands.TryParseHelpLine(line.Text, out var command))
            lock (_commands)
                if (!_commands.TryGetValue(command.Name, out var known) || known != command)
                {
                    _commands[command.Name] = command;
                    _dirty = true;
                }
    }

    /// <summary>Все известные команды по имени.</summary>
    public List<ServerCommand> Sorted()
    {
        lock (_commands) return _commands.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Из главного цикла: что-то поменялось — сохранить; не вышло — попробовать в следующий раз.</summary>
    public void SaveIfChanged()
    {
        if (!_dirty) return;
        lock (_commands)
        {
            _dirty = false;
            try { ServerCommands.Save(_file, _commands.Values); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _dirty = true; } // в следующий раз
        }
    }
}

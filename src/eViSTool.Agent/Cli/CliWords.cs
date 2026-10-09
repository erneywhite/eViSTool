using eViSTool.Core.Localization;

/// <summary>
/// Слова после команды: что осталось от «remote enable --host 1.2.3.4 --profile x». Общие ключи агента (--profile,
/// --agents-dir, --lang…) уже разобрал <see cref="AgentArgs"/> — здесь они пропускаются вместе со значением, свои ключи
/// команды собираются в <see cref="Options"/>, остальное — слова по порядку. Незнакомый ключ — ошибка: опечатка в
/// «--hots» не должна молча превратиться в «адрес не задан».
/// </summary>
internal sealed class CliWords
{
    // те же, что разбирает AgentArgs.Parse: появится там новый ключ со значением — добавить и сюда
    private static readonly HashSet<string> GlobalWithValue =
        ["--profile", "--exe", "--data", "--game", "--arg", "--idle-exit", "--agents-dir", "--lang", "--backup-name"];
    private static readonly HashSet<string> GlobalFlags = ["--start", "--after-update", "--help", "-h"];

    public List<string> Words { get; } = [];
    public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);

    /// <summary>Что не так со словами (для stderr); null — всё разобрано.</summary>
    public string? Error { get; private set; }

    public string? this[string option] => Options.GetValueOrDefault(option);

    /// <param name="args">слова после команды</param>
    /// <param name="options">свои ключи команды, у каждого — значение</param>
    public static CliWords Parse(IReadOnlyList<string> args, params string[] options)
    {
        var w = new CliWords();
        for (var i = 0; i < args.Count && w.Error is null; i++)
        {
            var a = args[i];
            if (GlobalWithValue.Contains(a)) i++;
            else if (GlobalFlags.Contains(a)) { }
            else if (options.Contains(a))
            {
                if (i + 1 < args.Count) w.Options[a] = args[++i];
                else w.Error = Loc.T("ctl.needValue", a);
            }
            else if (a.StartsWith("--", StringComparison.Ordinal)) w.Error = Loc.T("ctl.unknownOption", a);
            else w.Words.Add(a);
        }
        return w;
    }

    /// <summary>Слов больше, чем ждёт команда — сказать, какие лишние.</summary>
    public bool TooMany(int expected)
    {
        if (Words.Count <= expected) return false;
        Error = Loc.T("ctl.extraWords", string.Join(" ", Words.Skip(expected)));
        return true;
    }

    /// <summary>Неверные слова: причина и справка команды — в stderr, код 2.</summary>
    public int Bad(string usage)
    {
        if (Error is not null) Console.Error.WriteLine(Error);
        Console.Error.WriteLine(usage);
        return Commands.BadUsage;
    }

    /// <summary>Секунды из ключа (--timeout 60); нет ключа — по умолчанию; не число — null.</summary>
    public TimeSpan? Seconds(string option, TimeSpan fallback)
    {
        if (this[option] is not { } text) return fallback;
        if (int.TryParse(text, out var s) && s > 0) return TimeSpan.FromSeconds(s);
        Error = Loc.T("ctl.badSeconds", option);
        return null;
    }
}

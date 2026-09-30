namespace eViSTool.Core.Server.Config;

/// <summary>Чем поле редактируется и как его значение превращается в текст и обратно.</summary>
public enum ConfigValueKind { Text, Multiline, Integer, Decimal, Bool, Choice, StringList, Path, Json, ReadOnly }

/// <summary>Вариант для Choice: Value — то, что пишется в JSON (для числовых — "0", "1"…), LabelKey — ключ локализации.</summary>
public sealed record ConfigChoice(string Value, string LabelKey);

/// <summary>Описание одного поля serverconfig.json: где оно лежит, в каком разделе показывается и как разбирается.</summary>
public sealed record ConfigFieldSpec
{
    /// <summary>"Port", "WorldConfig.WorldName" — точка разделяет вложенные объекты.</summary>
    public required string Path { get; init; }

    /// <summary>Раздел редактора: <see cref="ConfigSections"/>.</summary>
    public required string Section { get; init; }

    /// <summary>Группа внутри раздела: "identity", "network", …</summary>
    public required string Group { get; init; }

    public required ConfigValueKind Kind { get; init; }

    public IReadOnlyList<ConfigChoice> Choices { get; init; } = [];

    /// <summary>
    /// Пустой ввод → null в JSON. Схема подстраивает признак под файл (см. <see cref="ServerConfigSchema.FieldsFor"/>):
    /// пустой ввод возвращает ту «пустоту», что лежала в файле, — если поле не помечено <see cref="EmptyIsNull"/> или <see cref="NeverNull"/>.
    /// </summary>
    public bool Nullable { get; init; }

    /// <summary>
    /// «Пусто» здесь — только null: пустой ввод (и из одних пробелов) даёт null, даже если в файле лежит "" или [].
    /// Так у адреса сервера: «все адреса» — это именно null.
    /// </summary>
    public bool EmptyIsNull { get; init; }

    /// <summary>
    /// null в этом поле роняет сервер: пустой ввод даёт "" (для списка — []), даже если в файле лежит null.
    /// </summary>
    public bool NeverNull { get; init; }

    /// <summary>Choice хранится числом.</summary>
    public bool NumericChoice { get; init; }

    public decimal? Min { get; init; }
    public decimal? Max { get; init; }

    /// <summary>Поле из схемы (есть перевод подписи); иначе подпись — имя поля.</summary>
    public bool Known { get; init; }

    /// <summary>Текстовое поле нельзя оставить пустым: с пустым значением сервер не заработает как надо (имя сервера, файл мира).</summary>
    public bool Required { get; init; }

    /// <summary>Сервер читает поле только при создании мира: для уже созданного мира правка ничего не изменит.</summary>
    public bool WorldCreationOnly { get; init; }

    /// <summary>Устаревшее или не используемое сервером поле — показывается с пометкой.</summary>
    public bool Legacy { get; init; }

    /// <summary>Последний сегмент пути — имя поля в JSON.</summary>
    public string Name => Path[(Path.LastIndexOf('.') + 1)..];

    public string LabelKey => "cfg." + Path;

    /// <summary>Подсказка под полем; в словаре её может не быть.</summary>
    public string HintKey => LabelKey + ".hint";

    public string GroupKey => "cfg.group." + Group;
}

public static class ConfigSections
{
    public const string General = "general", World = "world", Advanced = "advanced";
}

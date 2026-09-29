using eViSTool.Core.Versioning;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace eViSTool.Core.Profiles;

public enum ProfileKind
{
    /// <summary>Клиент игры: настройки в clientsettings.json.</summary>
    Client,
    /// <summary>Выделенный сервер: настройки в serverconfig.json.</summary>
    Server,
}

/// <summary>Профиль — одна установка игры/сервера с её папкой данных.</summary>
public sealed class GameProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    [JsonConverter(typeof(StringEnumConverter))]
    public ProfileKind Kind { get; set; }

    /// <summary>Папка игры/сервера (Vintagestory.exe / VintagestoryServer.exe).</summary>
    public string? GameDir { get; set; }

    /// <summary>Папка данных (VintagestoryData или --dataPath сервера).</summary>
    public string? DataDir { get; set; }

    public override string ToString() => Name;
}

/// <summary>Профиль, разобранный по файлам игры: версия, папки модов, выключенные моды.</summary>
public sealed record ResolvedProfile
{
    public required GameProfile Profile { get; init; }
    public ModVersion? GameVersion { get; init; }

    /// <summary>clientsettings.json / serverconfig.json, если найден.</summary>
    public string? ConfigPath { get; init; }

    /// <summary>Папки, откуда игра грузит моды (без встроенной папки игры).</summary>
    public IReadOnlyList<string> ModDirs { get; init; } = [];

    /// <summary>Куда ставить новые моды — первая существующая папка из ModDirs.</summary>
    public string? InstallDir { get; init; }

    /// <summary>Выключенные моды в формате игры: "modid" или "modid@версия".</summary>
    public IReadOnlyList<string> DisabledMods { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

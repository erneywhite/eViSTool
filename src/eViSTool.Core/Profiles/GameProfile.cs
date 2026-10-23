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

/// <summary>Где живёт профиль: на этой машине, на другом компьютере (через агента) или у хостера (по FTP).</summary>
public enum ProfileHost { Local, Agent, Hosted }

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

    /// <summary>
    /// Удалённый сервер: код подключения к агенту на другой машине, зашифрованный (<see cref="Server.Remote.RemoteSecret"/>).
    /// У такого профиля нет своих папок — всё идёт через агента по сети.
    /// </summary>
    public string? RemoteCode { get; set; }

    /// <summary>
    /// Метка в <see cref="RemoteCode"/> у сервера у хостера. Окно до 0.12 примет её за код агента, не расшифрует
    /// и покажет «код не читается»: сервер не запустит и в локальные папки не полезет.
    /// </summary>
    public const string HostedMarker = "hosted";

    /// <summary>Сервер у хостера (G-Portal и другие): моды по FTP. null — профиль не у хостера или подключение
    /// потеряно (профиль сохранило старое окно — тогда остаётся только <see cref="HostedMarker"/>).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Hosting.HostedConnection? Hosted { get; set; }

    /// <summary>Где живёт профиль. <see cref="Kind"/> у сервера у хостера — Server: новое значение Kind старое окно не
    /// прочитало бы и сбросило бы все настройки.</summary>
    [JsonIgnore]
    public ProfileHost Host => Hosted is not null || RemoteCode == HostedMarker ? ProfileHost.Hosted
        : string.IsNullOrEmpty(RemoteCode) ? ProfileHost.Local : ProfileHost.Agent;

    /// <summary>Сервер на другом компьютере, через его агента (не у хостера).</summary>
    [JsonIgnore]
    public bool IsRemote => Host == ProfileHost.Agent;

    [JsonIgnore]
    public bool IsHosted => Host == ProfileHost.Hosted;

    /// <summary>Профиль на этой машине: свои папки игры и данных.</summary>
    [JsonIgnore]
    public bool IsLocal => Host == ProfileHost.Local;

    /// <summary>Цвет значка профиля — имя из <see cref="ProfileIcon.Colors"/>; null — нейтральная плашка.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? IconColor { get; set; }

    /// <summary>Значок профиля — имя из <see cref="ProfileIcon.Glyphs"/>; null — значок вида (клиент, сервер, хостер).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? IconGlyph { get; set; }

    /// <summary>Закреплённые моды: modid → версия, на которой закреплён. Такие не обновляются.</summary>
    public Dictionary<string, string> PinnedMods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Пропущенные версии: modid → версии, которые не предлагать.</summary>
    public Dictionary<string, List<string>> BlockedVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Mods.ModPolicy ToPolicy() => new(PinnedMods, BlockedVersions);

    /// <summary>
    /// Связанные профили (id): моды, нужные обоим, предлагается ставить заодно — например, сервер и клиентский
    /// профиль, из которого на нём играют. Связь двусторонняя: хватает записи у одного из двух (см. <see cref="IsLinkedTo"/>).
    /// </summary>
    public List<string> LinkedProfiles { get; set; } = [];

    /// <summary>
    /// Клиентский профиль: к какому серверу подключаться по кнопке «Играть» (<see cref="Game.PlayTarget.Key"/>);
    /// null — просто запустить игру.
    /// </summary>
    public string? DefaultServer { get; set; }

    /// <summary>Когда из окна последний раз запускали игру (клиент) или сервер; null — ни разу. Для сортировки списка.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public DateTime? LastPlayed { get; set; }

    public bool IsLinkedTo(GameProfile other) =>
        LinkedProfiles.Contains(other.Id, StringComparer.Ordinal) || other.LinkedProfiles.Contains(Id, StringComparer.Ordinal);

    /// <summary>Связать или развязать (с обеих сторон: развязка убирает запись и у второго профиля).</summary>
    public void SetLinked(GameProfile other, bool linked)
    {
        LinkedProfiles.RemoveAll(id => id == other.Id);
        other.LinkedProfiles.RemoveAll(id => id == Id);
        if (linked) LinkedProfiles.Add(other.Id);
    }

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

    /// <summary>
    /// Папки и файлы профиля не на этой машине (агент, хостер): пути в нём — чужие. Страховка: по такому профилю
    /// ничего не читается и не пишется на локальном диске (скан — пусто, отметка — null, правка конфига — исключение).
    /// </summary>
    public bool OffMachine { get; init; }
}

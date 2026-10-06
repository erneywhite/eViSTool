using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Profiles;
using eViSTool.Core.Versioning;

namespace eViSTool.Core.Mods;

public enum AlsoInstallState
{
    /// <summary>Можно поставить: релиз подобран под версию игры профиля.</summary>
    Ready,
    /// <summary>Можно поставить, но версия игры профиля неизвестна — совместимость не проверена.</summary>
    UnknownGame,
    /// <summary>Эта версия уже стоит.</summary>
    AlreadyThere,
    /// <summary>Под ветку игры профиля релиза нет.</summary>
    NoRelease,
    /// <summary>Не удалось прочитать профиль (удалённый сервер не на связи и т. п.).</summary>
    Unavailable,
}

/// <summary>Что будет, если поставить мод ещё и в этот профиль.</summary>
public sealed record AlsoInstallOption(ModTarget Target, AlsoInstallState State, ModVersion? Game, string? Installed,
    ModDbRelease? Release, string? Error = null)
{
    public bool CanInstall => State is AlsoInstallState.Ready or AlsoInstallState.UnknownGame && Release?.MainFile is not null;

    /// <summary>Строка под именем профиля: игра, что стоит, что встанет.</summary>
    public string Describe()
    {
        var game = AlsoInstall.GameText(Game);
        return State switch
        {
            AlsoInstallState.Unavailable => Loc.T("also.unavailable", Error),
            AlsoInstallState.NoRelease => Loc.T("also.noRelease", game, Game is { } g ? $"{g.Major}.{g.Minor}.x" : "?"),
            AlsoInstallState.AlreadyThere => Loc.T("also.already", game, Installed),
            _ => Installed is null
                ? Loc.T("also.willInstall", game, Release!.ModVersion)
                : Loc.T("also.willUpdate", game, Installed, Release!.ModVersion),
        } + (State == AlsoInstallState.UnknownGame ? " " + Loc.T("also.unchecked") : "");
    }
}

/// <summary>
/// «Установить также в»: мод, который ставят в один профиль, — ещё и в другие (сервер ↔ клиент).
/// Каждому профилю — свой релиз: выбранный, если он подходит к версии его игры, иначе лучший для неё.
/// Предлагается только туда, где мод нужен (клиентский мод выделенному серверу не нужен), и только для модов,
/// которые нужны игроку на клиенте или серверу у игрока (сторона «клиент», «оба»).
/// </summary>
public static class AlsoInstall
{
    /// <summary>«игра 1.22.7» или «версия игры неизвестна».</summary>
    public static string GameText(ModVersion? game) => game is not null ? Loc.T("also.game", game) : Loc.T("also.gameUnknown");

    /// <summary>Стоит ли вообще спрашивать про другие профили для мода с такой стороной.</summary>
    public static bool WorthOffering(ModSide side) => side != ModSide.Server;

    /// <summary>Нужен ли мод с этой стороной в профиле такого типа.</summary>
    public static bool Needed(ModSide side, ProfileKind kind) => !ModSides.IsUnneeded(side, kind);

    /// <summary>
    /// Файлы модов (добавленные вручную) — в другой профиль: какие из них туда нужны и ещё не стоят в этой версии.
    /// Версию игры по файлу не проверить — совместимость решает тот, кто их добавляет.
    /// </summary>
    public static async Task<AlsoInstallFiles> EvaluateFilesAsync(ModTarget target, IReadOnlyList<(string Path, ModInfo Info)> files,
        CancellationToken ct = default)
    {
        var wanted = files.Where(f => Needed(ModSides.Parse(f.Info.Side), target.Profile.Kind)).ToList();
        try
        {
            var (resolved, mods) = await ModTargets.ScanAsync(target, ct).ConfigureAwait(false);
            var have = mods.Where(m => m.Info is not null)
                .GroupBy(m => m.Info!.ModId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Info!.Version, StringComparer.OrdinalIgnoreCase);
            var toInstall = wanted.Where(f => !(have.TryGetValue(f.Info.ModId, out var v)
                                                && ModVersion.ParseOrNull(v) is { } a && ModVersion.ParseOrNull(f.Info.Version) is { } b
                                                && a.CompareTo(b) == 0)).ToList();
            return new AlsoInstallFiles(target, resolved.GameVersion, toInstall, wanted.Count - toInstall.Count, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException
                                       or TaskCanceledException or Newtonsoft.Json.JsonException)
        {
            return new AlsoInstallFiles(target, null, [], 0, target.IsRemote ? Server.Remote.RemoteSecret.Describe(ex) : ex.Message);
        }
    }

    public static async Task<AlsoInstallOption> EvaluateAsync(ModTarget target, string modId, IReadOnlyList<ModDbRelease> releases,
        ModDbRelease chosen, CancellationToken ct = default)
    {
        ResolvedProfile resolved;
        IReadOnlyList<LocalMod> mods;
        try
        {
            (resolved, mods) = await ModTargets.ScanAsync(target, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException
                                       or TaskCanceledException or Newtonsoft.Json.JsonException)
        {
            return new AlsoInstallOption(target, AlsoInstallState.Unavailable, null, null, null,
                target.IsRemote ? Server.Remote.RemoteSecret.Describe(ex) : ex.Message);
        }

        var game = resolved.GameVersion;
        var installed = mods.FirstOrDefault(m => string.Equals(m.Info?.ModId, modId, StringComparison.OrdinalIgnoreCase))?.Info?.Version;
        var release = game is null || CatalogPick.Fits(chosen, game) ? chosen : CatalogPick.Best(releases, game).Release;
        if (release is null) return new AlsoInstallOption(target, AlsoInstallState.NoRelease, game, installed, null);

        var same = ModVersion.ParseOrNull(installed) is { } have && ModVersion.ParseOrNull(release.ModVersion) is { } want
            && have.CompareTo(want) == 0;
        var state = same ? AlsoInstallState.AlreadyThere : game is null ? AlsoInstallState.UnknownGame : AlsoInstallState.Ready;
        return new AlsoInstallOption(target, state, game, installed, release);
    }
}

/// <summary>Файлы модов для другого профиля: что туда встанет; сколько уже стоит; ошибка — профиль не прочитать.</summary>
public sealed record AlsoInstallFiles(ModTarget Target, ModVersion? Game, IReadOnlyList<(string Path, ModInfo Info)> ToInstall,
    int AlreadyThere, string? Error)
{
    public bool CanInstall => Error is null && ToInstall.Count > 0;

    public string Describe() => Error is not null ? Loc.T("also.unavailable", Error)
        : ToInstall.Count == 0 ? Loc.T("also.allThere", AlsoInstall.GameText(Game))
        : Loc.T("also.files", AlsoInstall.GameText(Game), string.Join(", ", ToInstall.Select(f => $"{f.Info.Name} {f.Info.Version}")))
          + (AlreadyThere > 0 ? " " + Loc.T("also.filesSome", AlreadyThere) : "");
}

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

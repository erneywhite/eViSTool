using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;

namespace eViSTool.Core.Mods;

/// <summary>
/// Какие обновления модов цели вышли — без скачивания: для оповещения «вышли обновления модов» и плашки «Обновить всё»
/// у сервера у хостера. Проверка — тем же путём, что «Проверить» в «Моих модах»: моды цели, модбаза, оценка с
/// закреплениями и пропущенными версиями профиля. Проверка идёт в фоне, поэтому у хостера моды читаются сканом, который
/// ничего не меняет (<see cref="HostedMods.PeekAsync"/>): починку и её разовые предупреждения он оставляет «Моим модам».
/// </summary>
public static class ModUpdatesDue
{
    /// <summary>Моды цели и модбаза; версия игры цели неизвестна — <see cref="InvalidOperationException"/>.</summary>
    public static async Task<IReadOnlyList<ModCheckResult>> CheckAsync(ModTarget target, ModUpdateService service, bool allowUnstable,
        CancellationToken ct = default)
    {
        var (resolved, mods) = target.IsHosted
            ? await new HostedMods(target).PeekAsync(ct).ConfigureAwait(false)
            : await ModTargets.ScanAsync(target, ct).ConfigureAwait(false);
        var game = resolved.GameVersion ?? throw new InvalidOperationException(Loc.T("err.noGameVersion"));
        var remote = await service.FetchRemoteAsync(mods, ct: ct).ConfigureAwait(false);
        return Of(UpdateChecker.Evaluate(mods, remote, game, allowUnstable, target.Profile.ToPolicy()), target.IsHosted);
    }

    /// <summary>
    /// Из оценки — то, что можно поставить: релиз новее, под версию игры и с файлом. По названию. У сервера у хостера
    /// (<paramref name="hosted"/>) — без модов только для чтения (мод хостера, распакованная папка): их не берёт и
    /// «Обновить всё» в «Моих модах».
    /// </summary>
    public static IReadOnlyList<ModCheckResult> Of(IEnumerable<ModCheckResult> results, bool hosted = false) =>
        [.. results.Where(r => r.Status == ModStatus.UpdateAvailable && r.LatestCompatible?.MainFile is not null && r.Local.Info is not null
                               && !(hosted && HostedMods.ReadOnlyReason(r.Local) is not null))
            .OrderBy(Line, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Название мода (нет — modid).</summary>
    public static string Name(ModCheckResult r) => string.IsNullOrWhiteSpace(r.Local.Info!.Name) ? r.Local.Info.ModId : r.Local.Info.Name;

    /// <summary>«Название: 1.2.0 → 1.3.0» — как обновление называет оповещение агента.</summary>
    public static string Line(ModCheckResult r) => $"{Name(r)}: {r.Local.Info!.Version} → {r.LatestCompatible!.ModVersion}";
}

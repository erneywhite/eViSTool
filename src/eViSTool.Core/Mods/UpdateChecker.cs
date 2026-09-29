using eViSTool.Core.ModDb;
using eViSTool.Core.Versioning;

namespace eViSTool.Core.Mods;

public enum ModStatus
{
    /// <summary>Установлена актуальная версия.</summary>
    UpToDate,
    /// <summary>Есть более новая версия для текущей ветки игры.</summary>
    UpdateAvailable,
    /// <summary>В модбазе нет релиза для этой ветки игры.</summary>
    NoCompatibleRelease,
    /// <summary>Мод не найден в модбазе (локальный, удалён или другой modid).</summary>
    NotInModDb,
    /// <summary>Не удалось прочитать мод (нет modinfo.json, битый архив).</summary>
    Unreadable,
    /// <summary>Модбаза не ответила.</summary>
    CheckFailed,
    /// <summary>С модбазой ещё не сверяли.</summary>
    NotChecked,
    /// <summary>Есть обновление, но версия закреплена пользователем.</summary>
    Pinned,
}

public sealed record ModCheckResult
{
    public required LocalMod Local { get; init; }
    public required ModStatus Status { get; init; }
    public ModDbMod? Remote { get; init; }

    /// <summary>Лучший релиз для текущей ветки игры.</summary>
    public ModDbRelease? LatestCompatible { get; init; }

    /// <summary>Самый новый релиз вообще — если он новее совместимого (например, уже под следующую версию игры).</summary>
    public ModDbRelease? LatestAny { get; init; }

    /// <summary>Тот же modid встречается в папке больше одного раза — игра загрузит только один.</summary>
    public bool IsDuplicate { get; init; }

    public string? Message { get; init; }
}

/// <summary>Сверяет установленные моды с модбазой.</summary>
public static class UpdateChecker
{
    /// <summary>
    /// Выбор релиза. Совместимость — по ветке игры (major.minor), а не по точной версии:
    /// авторы редко отмечают каждый патч, и мод с тегом 1.22.3 нормально работает на 1.22.7.
    /// </summary>
    public static ModDbRelease? PickLatestCompatible(IEnumerable<ModDbRelease> releases, ModVersion gameVersion, bool allowUnstable,
        Func<ModVersion, bool>? skip = null)
    {
        return Candidates(releases, allowUnstable, skip)
            .Where(x => x.Release.GameVersions.Any(t => ModVersion.ParseOrNull(t)?.SameBranch(gameVersion) == true))
            .MaxBy(x => x.Version)?.Release;
    }

    public static ModDbRelease? PickLatest(IEnumerable<ModDbRelease> releases, bool allowUnstable, Func<ModVersion, bool>? skip = null) =>
        Candidates(releases, allowUnstable, skip).MaxBy(x => x.Version)?.Release;

    /// <summary>Релизы для ветки игры, новые сверху (для окна отката).</summary>
    public static IReadOnlyList<ModDbRelease> CompatibleReleases(IEnumerable<ModDbRelease> releases, ModVersion gameVersion) =>
        Candidates(releases, allowUnstable: true, skip: null)
            .Where(x => x.Release.GameVersions.Any(t => ModVersion.ParseOrNull(t)?.SameBranch(gameVersion) == true))
            .OrderByDescending(x => x.Version)
            .Select(x => x.Release)
            .ToList();

    private sealed record Candidate(ModDbRelease Release, ModVersion Version);

    private static IEnumerable<Candidate> Candidates(IEnumerable<ModDbRelease> releases, bool allowUnstable, Func<ModVersion, bool>? skip)
    {
        foreach (var r in releases)
        {
            if (!ModVersion.TryParse(r.ModVersion, out var v)) continue;
            if (v.IsPrerelease && !allowUnstable) continue;
            if (skip?.Invoke(v) == true) continue;
            yield return new Candidate(r, v);
        }
    }

    public static IReadOnlyList<ModCheckResult> Evaluate(
        IReadOnlyList<LocalMod> localMods,
        IReadOnlyDictionary<string, ModDbResult> remote,
        ModVersion gameVersion,
        bool allowUnstable = false,
        ModPolicy? policy = null)
    {
        policy ??= ModPolicy.Empty;
        var duplicates = localMods
            .Where(m => m.Info is not null)
            .GroupBy(m => m.Info!.ModId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return localMods.Select(local =>
        {
            if (local.Info is null)
                return new ModCheckResult { Local = local, Status = ModStatus.Unreadable, Message = local.Error };

            var dup = duplicates.Contains(local.Info.ModId);

            if (!remote.TryGetValue(local.Info.ModId, out var r))
                return new ModCheckResult { Local = local, Status = ModStatus.NotChecked, IsDuplicate = dup };
            if (r.Error is not null)
                return new ModCheckResult { Local = local, Status = ModStatus.CheckFailed, IsDuplicate = dup, Message = r.Error };
            if (r.Mod is null)
                return new ModCheckResult { Local = local, Status = ModStatus.NotInModDb, IsDuplicate = dup };

            var installed = ModVersion.ParseOrNull(local.Info.Version);
            // стоит пре-релиз — значит, человек сознательно на нестабильной ветке этого мода
            var unstable = allowUnstable || installed?.IsPrerelease == true;

            var modId = local.Info.ModId;
            bool Skip(ModVersion v) => policy.IsBlocked(modId, v);
            var compatible = PickLatestCompatible(r.Mod.Releases, gameVersion, unstable, Skip);
            var latest = PickLatest(r.Mod.Releases, unstable, Skip);
            var latestVersion = ModVersion.ParseOrNull(latest?.ModVersion);

            // самый новый релиз интересен, только если он новее и совместимого, и установленного
            // (у выбранных релизов версия гарантированно разбирается, см. Candidates)
            var newerElsewhere = latest is not null && latest != compatible
                && (compatible is null || latestVersion!.CompareTo(ModVersion.ParseOrNull(compatible.ModVersion)) > 0)
                && (installed is null || latestVersion!.CompareTo(installed) > 0)
                ? latest : null;

            ModStatus status;
            string? message = null;
            var branch = $"{gameVersion.Major}.{gameVersion.Minor}.x";
            if (compatible is null && installed is not null && latestVersion is not null && installed >= latestVersion)
            {
                // автор просто не отметил текущую ветку игры, а новее ничего нет
                status = ModStatus.UpToDate;
                message = $"Последняя версия, но автор не отметил совместимость с {branch}";
            }
            else if (compatible is null)
            {
                status = ModStatus.NoCompatibleRelease;
                message = $"Нет релиза для {branch}";
            }
            else if (installed is null)
            {
                // версию не разобрать — считаем, что стоит не то, что надо
                status = ModStatus.UpdateAvailable;
                message = "Не удалось разобрать установленную версию";
            }
            else
            {
                var target = ModVersion.ParseOrNull(compatible.ModVersion)!;
                status = target > installed ? ModStatus.UpdateAvailable : ModStatus.UpToDate;
                if (installed > target) message = "Установлена версия новее, чем в модбазе";
                if (status == ModStatus.UpdateAvailable && policy.IsPinned(modId))
                {
                    status = ModStatus.Pinned;
                    message = $"Закреплён на {installed}, в модбазе есть {target}";
                }
            }

            return new ModCheckResult
            {
                Local = local,
                Status = status,
                Remote = r.Mod,
                LatestCompatible = compatible,
                LatestAny = newerElsewhere,
                IsDuplicate = dup,
                Message = message,
            };
        }).ToList();
    }
}

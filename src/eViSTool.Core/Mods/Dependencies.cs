using eViSTool.Core.Versioning;
using eViSTool.Core.Localization;

namespace eViSTool.Core.Mods;

/// <summary>Проблема с зависимостью: мода нет, он выключен или его версия ниже требуемой.</summary>
public sealed record DependencyIssue(
    string ModId,
    string RequiredVersion,
    IReadOnlyList<string> RequiredBy,
    string? InstalledVersion,
    bool IsDisabled)
{
    public bool IsMissing => InstalledVersion is null;
    public bool IsOutdated => !IsMissing && !IsDisabled;

    public string Describe() =>
        IsMissing ? Loc.T("dep.missing", ModId, string.Join(", ", RequiredBy))
        : IsDisabled ? Loc.T("dep.disabled", ModId, string.Join(", ", RequiredBy))
        : Loc.T("dep.outdated", ModId, InstalledVersion, string.Join(", ", RequiredBy), RequiredVersion);
}

public static class Dependencies
{
    /// <summary>
    /// Зависимости включённых модов, которых не хватает. Как у Rustique: базовая игра
    /// (game/survival/creative) пропускается, modid сравниваются без учёта регистра.
    /// Версия в dependencies — минимальная ("" или "*" — любая).
    /// </summary>
    public static IReadOnlyList<DependencyIssue> FindIssues(IReadOnlyList<LocalMod> locals)
    {
        var byId = locals.Where(l => l.Info is not null)
            .GroupBy(l => l.Info!.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var issues = new Dictionary<string, (string Required, List<string> By, string? Installed, bool Disabled)>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in locals.Where(l => l.Info is not null && !l.IsDisabled))
        {
            foreach (var (depId, required) in mod.Info!.Dependencies)
            {
                if (ModInfo.IsBaseGame(depId)) continue;
                var name = string.IsNullOrWhiteSpace(mod.Info.Name) ? mod.Info.ModId : mod.Info.Name;

                string? installed = null;
                var disabled = false;
                if (byId.TryGetValue(depId, out var copies))
                {
                    var enabled = copies.Where(c => !c.IsDisabled).ToList();
                    if (enabled.Count == 0)
                    {
                        disabled = true;
                        installed = copies[0].Info!.Version;
                    }
                    else
                    {
                        var best = enabled.Select(c => ModVersion.ParseOrNull(c.Info!.Version)).Where(v => v is not null).Max();
                        var need = ModVersion.ParseOrNull(required);
                        if (need is null || best is null || best.CompareTo(need) >= 0) continue; // всё в порядке
                        installed = best.ToString();
                    }
                }

                var key = depId.ToLowerInvariant();
                if (issues.TryGetValue(key, out var existing))
                {
                    existing.By.Add(name);
                    // среди требований — самое строгое
                    if (ModVersion.ParseOrNull(required) is { } r && (ModVersion.ParseOrNull(existing.Required) is not { } e || r.CompareTo(e) > 0))
                        existing = existing with { Required = required };
                    issues[key] = existing;
                }
                else
                {
                    issues[key] = (required, [name], installed, disabled);
                }
            }
        }

        return issues.Select(kv => new DependencyIssue(kv.Key, kv.Value.Required, kv.Value.By, kv.Value.Installed, kv.Value.Disabled))
            .OrderBy(i => i.ModId)
            .ToList();
    }
}

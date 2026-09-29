using eViSTool.Core.ModDb;
using eViSTool.Core.Versioning;

namespace eViSTool.Core.Mods;

/// <summary>Проверка обновлений для папки модов целиком.</summary>
public sealed class ModUpdateService(ModDbClient db)
{
    public async Task<IReadOnlyList<ModCheckResult>> CheckAsync(
        string modsDir, ModVersion gameVersion, bool allowUnstable = false,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Чтение папки модов…");
        var locals = await Task.Run(() => ModScanner.Scan(modsDir), ct).ConfigureAwait(false);
        var identified = locals.Where(l => l.Info is not null).ToList();

        progress?.Report($"Запрос модбазы: 0/{identified.Count}");
        var perMod = new Progress<int>(n => progress?.Report($"Запрос модбазы: {n}/{identified.Count}"));
        var remote = new Dictionary<string, ModDbResult>(
            await db.GetModsAsync(identified.Select(l => l.Info!.ModId), progress: perMod, ct: ct).ConfigureAwait(false),
            StringComparer.OrdinalIgnoreCase);

        var notFound = identified.Where(l => remote.TryGetValue(l.Info!.ModId, out var r) && r is { Mod: null, Error: null }).ToList();
        if (notFound.Count > 0)
        {
            progress?.Report("Поиск ненайденных модов в каталоге…");
            await ResolveViaCatalogAsync(notFound, remote, ct).ConfigureAwait(false);
        }

        progress?.Report("Готово");
        return UpdateChecker.Evaluate(locals, remote, gameVersion, allowUnstable);
    }

    /// <summary>
    /// Запасной путь, как у Rustique: modid в modinfo.json иногда не совпадает с модбазой
    /// (xSkills Gilded: "xskillsgilded" против "xskillgilded"). Ищем в каталоге по modid и по имени.
    /// </summary>
    private async Task ResolveViaCatalogAsync(List<LocalMod> notFound, Dictionary<string, ModDbResult> remote, CancellationToken ct)
    {
        IReadOnlyList<ModDbListItem> catalog;
        try
        {
            catalog = await db.GetAllModsAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return; // без каталога просто останутся «не найдены»
        }

        foreach (var local in notFound)
        {
            var match = FindInCatalog(catalog, local.Info!);
            if (match is null) continue;

            try
            {
                var mod = await db.GetModAsync(match.ModId.ToString(), ct).ConfigureAwait(false);
                if (mod is not null) remote[local.Info!.ModId] = new ModDbResult(mod, null);
            }
            catch (HttpRequestException)
            {
                // оставляем как было
            }
        }
    }

    public static ModDbListItem? FindInCatalog(IReadOnlyList<ModDbListItem> catalog, ModInfo info)
    {
        static ModDbListItem? Single(IEnumerable<ModDbListItem> items)
        {
            var list = items.Take(2).ToList();
            return list.Count == 1 ? list[0] : null; // неоднозначность хуже, чем «не найдено»
        }

        return Single(catalog.Where(c => c.ModIdStrs.Any(s => string.Equals(s, info.ModId, StringComparison.OrdinalIgnoreCase))))
            ?? (string.IsNullOrWhiteSpace(info.Name) ? null
                : Single(catalog.Where(c => string.Equals(c.Name?.Trim(), info.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                  ?? Single(catalog.Where(c => c.Name is not null && ModInfo.ModIdFromName(c.Name) == ModInfo.ModIdFromName(info.Name))));
    }
}

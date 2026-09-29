using eViSTool.Core.ModDb;
using eViSTool.Core.Versioning;
using eViSTool.Core.Localization;

namespace eViSTool.Core.Mods;

/// <summary>Скачивание модов из модбазы: обновления, откат на релиз, зависимости.</summary>
public sealed class ModUpdater(ModDbClient db, string? downloadDir = null)
{
    public string DownloadDir { get; } = downloadDir ?? AppPaths.Downloads;

    /// <summary>
    /// Скачивает релиз во временную папку и проверяет, что это действительно нужный мод.
    /// Возвращает путь к zip; папку потом удалить через <see cref="Cleanup"/>.
    /// </summary>
    public async Task<string> DownloadReleaseAsync(ModDbRelease release, string expectedModId,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var url = release.MainFile ?? throw new InvalidDataException(Loc.T("dl.noFile", release.ModVersion));
        var name = SafeFileName(release.FileName) ?? $"{expectedModId}_{release.ModVersion}.zip";
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name += ".zip";

        // своя подпапка на каждое скачивание — одинаковые имена файлов не мешают друг другу
        var dest = Path.Combine(DownloadDir, Guid.NewGuid().ToString("N")[..8], name);
        await db.DownloadAsync(url, dest, progress, ct).ConfigureAwait(false);

        var check = ModScanner.ReadZip(dest);
        if (check.Info is null)
        {
            Cleanup(dest);
            throw new InvalidDataException(Loc.T("dl.unreadable", name, check.Error));
        }
        if (!string.Equals(check.Info.ModId, expectedModId, StringComparison.OrdinalIgnoreCase))
        {
            Cleanup(dest);
            throw new InvalidDataException(Loc.T("dl.wrongMod", check.Info.ModId, expectedModId));
        }
        return dest;
    }

    /// <summary>Лучший релиз мода для ветки игры с учётом закреплений/пропусков. null — нет в модбазе или нет релиза для ветки.</summary>
    public async Task<(ModDbMod Mod, ModDbRelease Release)?> FindBestReleaseAsync(
        string modId, ModVersion gameVersion, bool allowUnstable, ModPolicy policy, CancellationToken ct = default)
    {
        var mod = await db.GetModAsync(modId, ct).ConfigureAwait(false);
        if (mod is null) return null;
        var release = UpdateChecker.PickLatestCompatible(mod.Releases, gameVersion, allowUnstable, v => policy.IsBlocked(modId, v))
                      // бывает, что у мода только пре-релизы под текущую ветку (CarryOnLib)
                      ?? UpdateChecker.PickLatestCompatible(mod.Releases, gameVersion, allowUnstable: true, v => policy.IsBlocked(modId, v));
        return release is null ? null : (mod, release);
    }

    /// <summary>Удаляет временную подпапку скачивания.</summary>
    public void Cleanup(string downloadedFile)
    {
        try
        {
            var dir = Path.GetDirectoryName(downloadedFile);
            if (dir is not null && dir.StartsWith(DownloadDir, StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // не страшно: временная папка, подчистится в следующий раз
        }
    }

    private static string? SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = Path.GetFileName(name);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}

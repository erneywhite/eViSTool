using System.IO.Compression;
using System.Security.Cryptography;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using Newtonsoft.Json;

namespace eViSTool.Core.Packs;

public sealed record PackExportOptions
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    /// <summary>Класть файлы модов внутрь (работает без интернета, но файл большой).</summary>
    public bool BundleFiles { get; init; } = true;
    /// <summary>Положить папку ModConfig (настройки модов).</summary>
    public bool IncludeModConfig { get; init; }
    /// <summary>Положить и выключенные моды (с пометкой «выключен»).</summary>
    public bool IncludeDisabled { get; init; }
}

/// <summary>Сборка .evpack из модов профиля.</summary>
public static class PackBuilder
{
    /// <param name="onModDb">modid модов, которые есть в модбазе: их можно не класть внутрь, импорт скачает сам.</param>
    public static PackManifest Build(ResolvedProfile profile, IReadOnlyList<LocalMod> locals, PackExportOptions options,
        IReadOnlySet<string> onModDb, string outputPath, string? createdWith = null)
    {
        var manifest = new PackManifest
        {
            Name = options.Name,
            Description = options.Description,
            CreatedWith = createdWith,
            GameVersion = profile.GameVersion?.ToString(),
            Kind = profile.Profile.Kind,
        };

        var tmp = outputPath + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var mod in locals.Where(l => l.Info is not null).OrderBy(l => l.Info!.Name, StringComparer.OrdinalIgnoreCase))
            {
                var info = mod.Info!;
                if (mod.IsDisabled && !options.IncludeDisabled) continue;
                if (!seen.Add(info.ModId)) continue; // дубликаты — только первый

                // распакованный мод (папка) кладём как zip — игра грузит zip так же
                var isDir = Directory.Exists(mod.Path);
                var fileName = isDir ? mod.FileName + ".zip" : mod.FileName;
                if (!usedNames.Add(fileName)) fileName = $"{info.ModId}_{fileName}";

                // моды не из модбазы (форки, свои) кладём внутрь всегда — скачать их неоткуда
                var bundle = options.BundleFiles || isDir || !onModDb.Contains(info.ModId);
                byte[]? bytes = isDir ? ZipDirectory(mod.Path) : null;

                string sha;
                if (bundle)
                {
                    var entry = zip.CreateEntry(PackManifest.ModsFolder + fileName, CompressionLevel.NoCompression); // моды уже сжаты
                    using var dst = entry.Open();
                    if (bytes is not null) dst.Write(bytes);
                    else using (var src = File.OpenRead(mod.Path)) src.CopyTo(dst);
                }
                sha = bytes is not null ? Convert.ToHexString(SHA256.HashData(bytes)) : Sha256Of(mod.Path);

                manifest.Mods.Add(new PackMod
                {
                    ModId = info.ModId,
                    Name = string.IsNullOrWhiteSpace(info.Name) ? info.ModId : info.Name,
                    Version = info.Version,
                    Enabled = !mod.IsDisabled,
                    FileName = fileName,
                    Sha256 = sha,
                    Bundled = bundle,
                });
            }

            if (options.IncludeModConfig && profile.Profile.DataDir is { } data)
            {
                var configDir = Path.Combine(data, "ModConfig");
                if (Directory.Exists(configDir))
                {
                    foreach (var file in Directory.EnumerateFiles(configDir, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(configDir, file).Replace('\\', '/');
                        zip.CreateEntryFromFile(file, PackManifest.ConfigFolder + rel, CompressionLevel.Optimal);
                    }
                    manifest.IncludesModConfig = true;
                }
            }

            using var w = new StreamWriter(zip.CreateEntry(PackManifest.FileName).Open());
            w.Write(JsonConvert.SerializeObject(manifest, Formatting.Indented));
        }

        File.Move(tmp, outputPath, overwrite: true);
        return manifest;
    }

    public static string Sha256Of(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    private static byte[] ZipDirectory(string dir)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                zip.CreateEntryFromFile(file, Path.GetRelativePath(dir, file).Replace('\\', '/'));
        }
        return ms.ToArray();
    }
}

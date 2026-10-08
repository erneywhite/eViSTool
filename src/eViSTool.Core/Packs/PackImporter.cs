using System.IO.Compression;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Versioning;
using Newtonsoft.Json;

namespace eViSTool.Core.Packs;

/// <summary>Открытый файл .evpack.</summary>
public sealed class PackFile : IDisposable
{
    private readonly ZipArchive _zip;

    public string Path { get; }
    public PackManifest Manifest { get; }

    private PackFile(string path, ZipArchive zip, PackManifest manifest)
    {
        Path = path;
        _zip = zip;
        Manifest = manifest;
    }

    public static PackFile Open(string path)
    {
        var zip = ZipFile.OpenRead(path);
        try
        {
            var entry = zip.GetEntry(PackManifest.FileName)
                        ?? throw new InvalidDataException(Loc.T("pack.notAPack", System.IO.Path.GetFileName(path)));
            using var reader = new StreamReader(entry.Open());
            var manifest = JsonConvert.DeserializeObject<PackManifest>(reader.ReadToEnd())
                           ?? throw new InvalidDataException(Loc.T("pack.notAPack", System.IO.Path.GetFileName(path)));
            if (manifest.Format > 1) throw new InvalidDataException(Loc.T("pack.newerFormat"));
            return new PackFile(path, zip, manifest);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    public bool HasFile(PackMod mod) => mod.Bundled && _zip.GetEntry(PackManifest.ModsFolder + mod.FileName) is not null;

    /// <summary>Достаёт файл мода из пака во временную папку.</summary>
    public string ExtractMod(PackMod mod, string tempDir)
    {
        var entry = _zip.GetEntry(PackManifest.ModsFolder + mod.FileName)
                    ?? throw new InvalidDataException(Loc.T("pack.fileMissing", mod.FileName));
        Directory.CreateDirectory(tempDir);
        var dest = System.IO.Path.Combine(tempDir, mod.FileName);
        entry.ExtractToFile(dest, overwrite: true);
        return dest;
    }

    public IEnumerable<ZipArchiveEntry> ConfigEntries =>
        _zip.Entries.Where(e => e.FullName.StartsWith(PackManifest.ConfigFolder, StringComparison.Ordinal) && e.Name.Length > 0);

    public void Dispose() => _zip.Dispose();
}

public enum PackItemAction
{
    Install,
    Update,
    Downgrade,
    Same,
    /// <summary>Тот же modid в паке второй раз — ставится только первая запись.</summary>
    Duplicate,
}

public sealed record PackItemPlan(PackMod Mod, LocalMod? Installed, PackItemAction Action);

public sealed record PackImportPlan(PackManifest Manifest, IReadOnlyList<PackItemPlan> Items, IReadOnlyList<LocalMod> NotInPack, string? GameVersionWarning);

public sealed record PackImportResult(IReadOnlyList<string> Done, IReadOnlyList<string> Problems);

/// <summary>Импорт .evpack в профиль.</summary>
public sealed class PackImporter(ModDbClient db, ModUpdater updater)
{
    /// <summary>Как получить мод, которого нет внутри пака (по умолчанию — ровно эта версия с модбазы). Подменяется в тестах.</summary>
    public Func<PackMod, CancellationToken, Task<string>>? Download { get; init; }

    /// <summary>
    /// Куда класть копии поставленных (проверенных) архивов модов — чтобы окно предложило «Установить также в»
    /// связанные профили. null — не класть. Каждый мод — в своей подпапке (одинаковые имена файлов не мешают).
    /// </summary>
    public string? KeepInstalledIn { get; init; }

    /// <summary>Что произойдёт: новые, обновляемые, откатываемые, уже стоящие; и какие моды профиля в паке не упомянуты.</summary>
    public static PackImportPlan Plan(PackManifest manifest, ResolvedProfile profile, IReadOnlyList<LocalMod> locals)
    {
        var byId = locals.Where(l => l.Info is not null)
            .GroupBy(l => l.Info!.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = manifest.Mods.Select(m =>
        {
            byId.TryGetValue(m.ModId, out var have);
            if (!seen.Add(m.ModId)) return new PackItemPlan(m, have, PackItemAction.Duplicate);
            var action = PackItemAction.Install;
            if (have is not null)
            {
                var c = ModVersion.ParseOrNull(have.Info!.Version)?.CompareTo(ModVersion.ParseOrNull(m.Version));
                action = c switch
                {
                    0 => PackItemAction.Same,
                    > 0 => PackItemAction.Downgrade,
                    _ => PackItemAction.Update,
                };
                // та же версия, но файл другой — ставим файл из пака, чтобы у всех было одинаково
                if (action == PackItemAction.Same && !Directory.Exists(have.Path)
                    && !string.Equals(PackBuilder.Sha256Of(have.Path), m.Sha256, StringComparison.OrdinalIgnoreCase))
                    action = PackItemAction.Update;
            }
            return new PackItemPlan(m, have, action);
        }).ToList();

        var inPack = manifest.Mods.Select(m => m.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notInPack = byId.Values.Where(l => !inPack.Contains(l.Info!.ModId)).OrderBy(l => l.Info!.Name).ToList();

        string? warning = null;
        var packGame = ModVersion.ParseOrNull(manifest.GameVersion);
        if (packGame is not null && profile.GameVersion is { } game && !packGame.SameBranch(game))
            warning = Loc.T("pack.gameMismatch", manifest.GameVersion, game);

        return new PackImportPlan(manifest, items, notInPack, warning);
    }

    /// <summary>
    /// Куда ставится пак: свой профиль (файлы на этой машине) или сервер на другой машине (через его агента).
    /// Проверки пака общие, разные — только эти четыре действия.
    /// </summary>
    private sealed record PackTarget(
        Func<string, CancellationToken, Task<string>> Install,           // поставить архив → «имя: было → стало»
        Func<CancellationToken, Task<IReadOnlyList<LocalMod>?>> Scan,      // моды после установки; null — переключать нельзя
        Func<LocalMod, bool, CancellationToken, Task<bool>> SetEnabled,   // true — состояние поменялось
        Func<PackFile, CancellationToken, Task<IReadOnlyList<string>>> ApplyConfig); // настройки модов → что не встало

    /// <summary>
    /// Применить план к своему профилю. mirror — выключить моды профиля, которых нет в паке (не удаляя).
    /// Всё заменяемое уходит в хранилище версий, прежние настройки модов — в архив там же.
    /// </summary>
    public Task<PackImportResult> ApplyAsync(PackFile pack, PackImportPlan plan, ResolvedProfile profile,
        bool mirror, bool applyConfig, ModBackupStore backups, IProgress<string>? progress = null, CancellationToken ct = default,
        Func<IReadOnlyList<string>, bool>? confirmChanged = null)
    {
        var target = new PackTarget(
            Install: (file, _) =>
            {
                var installPlan = ModInstaller.Plan(file, profile, ModUpdateService.ScanLocal(profile));
                ModInstaller.Apply(installPlan, backups);
                var info = installPlan.Incoming.Info!;
                var old = installPlan.Replaces.FirstOrDefault()?.Info?.Version;
                return Task.FromResult(old is null ? $"{info.Name} {info.Version}" : $"{info.Name}: {old} → {info.Version}");
            },
            Scan: _ =>
            {
                var fresh = ProfileResolver.Resolve(profile.Profile);
                return Task.FromResult<IReadOnlyList<LocalMod>?>(fresh.ConfigPath is null ? null : ModUpdateService.ScanLocal(fresh));
            },
            SetEnabled: (mod, enabled, _) => Task.FromResult(ModConfigEditor.SetEnabled(ProfileResolver.Resolve(profile.Profile), mod.Info!, enabled)),
            ApplyConfig: (p, _) =>
            {
                if (profile.Profile.DataDir is { } data) ApplyModConfig(p, System.IO.Path.Combine(data, "ModConfig"), backups);
                return Task.FromResult<IReadOnlyList<string>>([]);
            });
        return ApplyCoreAsync(pack, plan, target, mirror, applyConfig, progress, ct, confirmChanged);
    }

    /// <summary>
    /// Применить план к серверу на другой машине: моды уходят агенту по одному (прежние версии остаются там же, для
    /// отката), включение и выключение — тоже через агента. Настройки модов — по файлу через «Настройки модов»
    /// агента: прежние версии сохраняются у него; передать можно только текстовые файлы — остальные в отчёте.
    /// </summary>
    public Task<PackImportResult> ApplyToRemoteAsync(PackFile pack, PackImportPlan plan, ModTarget target,
        bool mirror, bool applyConfig, IProgress<string>? progress = null, CancellationToken ct = default,
        Func<IReadOnlyList<string>, bool>? confirmChanged = null)
    {
        if (target.Remote is not { } code) throw new InvalidOperationException("not a remote target");
        var pt = new PackTarget(
            Install: async (file, c) =>
            {
                var outcome = await ModTargets.InstallAsync(target, file, ct: c).ConfigureAwait(false);
                return outcome!.Text;
            },
            Scan: async c =>
            {
                var (_, mods) = await ModTargets.ScanAsync(target, c).ConfigureAwait(false);
                return mods;
            },
            SetEnabled: async (mod, enabled, c) =>
            {
                if (mod.IsDisabled == !enabled) return false;
                await ModTargets.SetEnabledAsync(target, mod, enabled, c).ConfigureAwait(false);
                return true;
            },
            ApplyConfig: async (p, c) =>
            {
                var skipped = new List<string>();
                var source = new RemoteModConfigSource(code);
                foreach (var entry in p.ConfigEntries)
                {
                    var rel = entry.FullName[PackManifest.ConfigFolder.Length..];
                    if (ModConfigs.KindOf(rel) is null)
                    {
                        skipped.Add(Loc.T("pack.configNotText", rel));
                        continue;
                    }
                    string text;
                    using (var reader = new StreamReader(entry.Open())) text = await reader.ReadToEndAsync(c).ConfigureAwait(false);
                    try
                    {
                        await source.SaveAsync(new ModConfigSaveRequest(rel, text, null), c).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
                    {
                        skipped.Add($"{rel}: {ex.Message}");
                    }
                }
                return skipped;
            });
        return ApplyCoreAsync(pack, plan, pt, mirror, applyConfig, progress, ct, confirmChanged);
    }

    /// <summary>
    /// Общая часть. Сначала все моды извлекаются или скачиваются и проверяются — цель до этого не меняется:
    /// в архиве должен быть тот мод и та версия, что в описании пака, и файл — тот же, что был у автора пака.
    /// Не тот мод или испорченный вложенный файл — пункт пропускается. Файл с модбазы, перезалитый автором мода, —
    /// решает <paramref name="confirmChanged"/> (список «имя версия»; null или false — такие пункты пропускаются).
    /// Отчёт — по тому, что реально установлено.
    /// </summary>
    private async Task<PackImportResult> ApplyCoreAsync(PackFile pack, PackImportPlan plan, PackTarget target,
        bool mirror, bool applyConfig, IProgress<string>? progress, CancellationToken ct,
        Func<IReadOnlyList<string>, bool>? confirmChanged)
    {
        var done = new List<string>();
        var problems = new List<string>();
        var temp = System.IO.Path.Combine(updater.DownloadDir, "pack-" + Guid.NewGuid().ToString("N")[..8]);
        var fetched = new List<string>();

        try
        {
            // ---- 1. получить и проверить всё — цель пока не трогаем
            var ready = new List<(PackItemPlan Item, string File, ModInfo Info)>();
            var changed = new List<(PackItemPlan Item, string File, ModInfo Info)>();
            foreach (var item in plan.Items.Where(i => i.Action is not (PackItemAction.Same or PackItemAction.Duplicate)))
            {
                ct.ThrowIfCancellationRequested();
                var m = item.Mod;
                progress?.Report(Loc.T("pack.checking", m.Name, m.Version));

                string file;
                try
                {
                    file = pack.HasFile(m) ? pack.ExtractMod(m, temp)
                        : await (Download ?? DownloadExactAsync)(m, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    problems.Add($"{m.Name} {m.Version}: {ex.Message}");
                    continue;
                }
                fetched.Add(file);

                var info = ModScanner.ReadZip(file).Info;
                if (info is null || !string.Equals(info.ModId, m.ModId, StringComparison.OrdinalIgnoreCase) || !SameVersion(info.Version, m.Version))
                {
                    problems.Add(Loc.T("pack.wrongMod", m.Name, m.Version, info is null ? "?" : $"{info.ModId} {info.Version}"));
                    continue;
                }
                if (!string.IsNullOrEmpty(m.Sha256) && !string.Equals(PackBuilder.Sha256Of(file), m.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    if (pack.HasFile(m)) problems.Add(Loc.T("pack.hashBroken", m.Name, m.Version)); // вложенный файл испорчен или подменён
                    else changed.Add((item, file, info));                                          // автор мода перезалил релиз
                    continue;
                }
                ready.Add((item, file, info));
            }

            if (changed.Count > 0)
            {
                if (confirmChanged?.Invoke([.. changed.Select(c => $"{c.Info.Name} {c.Info.Version}")]) == true) ready.AddRange(changed);
                else problems.AddRange(changed.Select(c => Loc.T("pack.hashSkipped", c.Info.Name, c.Info.Version)));
            }

            // ---- 2. установка проверенного
            foreach (var (item, file, info) in ready)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(Loc.T("pack.installing", info.Name, info.Version));
                try
                {
                    done.Add(await target.Install(file, ct).ConfigureAwait(false));
                    if (KeepInstalledIn is { } keep)
                    {
                        var dir = Directory.CreateDirectory(System.IO.Path.Combine(keep, info.ModId)).FullName;
                        File.Copy(file, System.IO.Path.Combine(dir, System.IO.Path.GetFileName(file)), overwrite: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException
                                               or HttpRequestException)
                {
                    problems.Add($"{info.Name}: {ex.Message}");
                }
            }
            foreach (var dup in plan.Items.Where(i => i.Action == PackItemAction.Duplicate))
                problems.Add(Loc.T("pack.duplicate", dup.Mod.Name, dup.Mod.Version));

            // включено/выключено — как в паке; при зеркалировании лишние выключаются
            var mods = await target.Scan(ct).ConfigureAwait(false);
            if (mods is not null)
            {
                var now = mods.Where(l => l.Info is not null)
                    .GroupBy(l => l.Info!.ModId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                // повторяющаяся запись не переопределяет первую
                foreach (var m in plan.Manifest.Mods.DistinctBy(m => m.ModId, StringComparer.OrdinalIgnoreCase))
                    if (now.TryGetValue(m.ModId, out var local))
                        await target.SetEnabled(local, m.Enabled, ct).ConfigureAwait(false);
                if (mirror)
                {
                    foreach (var extra in plan.NotInPack)
                        if (extra.Info is not null && now.TryGetValue(extra.Info.ModId, out var current)
                            && await target.SetEnabled(current, false, ct).ConfigureAwait(false))
                            done.Add(Loc.T("pack.disabledExtra", extra.Info.Name));
                }
            }
            else if (plan.Manifest.Mods.Any(m => !m.Enabled) || mirror)
            {
                problems.Add(Loc.T("pack.noConfigForToggles"));
            }

            if (applyConfig && plan.Manifest.IncludesModConfig)
            {
                progress?.Report(Loc.T("pack.applyingConfig"));
                problems.AddRange(await target.ApplyConfig(pack, ct).ConfigureAwait(false));
                done.Add(Loc.T("pack.configApplied"));
            }
        }
        finally
        {
            foreach (var file in fetched.Where(f => f.StartsWith(updater.DownloadDir, StringComparison.OrdinalIgnoreCase)
                                                     && !f.StartsWith(temp, StringComparison.OrdinalIgnoreCase)))
                updater.Cleanup(file);
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
        return new PackImportResult(done, problems);
    }

    /// <summary>Версии равны с точностью до записи: «1.0» и «1.0.0» — одно и то же.</summary>
    private static bool SameVersion(string? actual, string? expected) =>
        ModVersion.ParseOrNull(actual) is { } a && ModVersion.ParseOrNull(expected) is { } e
            ? a.CompareTo(e) == 0
            : string.Equals(actual?.Trim(), expected?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Скачать ровно ту версию, что в паке (не «последнюю совместимую»).</summary>
    private async Task<string> DownloadExactAsync(PackMod m, CancellationToken ct)
    {
        var mod = await db.GetModAsync(m.ModId, ct).ConfigureAwait(false)
                  ?? throw new InvalidDataException(Loc.T("pack.notOnModDb"));
        var want = ModVersion.ParseOrNull(m.Version);
        var release = mod.Releases.FirstOrDefault(r => want is not null && ModVersion.ParseOrNull(r.ModVersion)?.CompareTo(want) == 0)
                      ?? throw new InvalidDataException(Loc.T("pack.versionGone", m.Version));
        return await updater.DownloadReleaseAsync(release, m.ModId, null, ct).ConfigureAwait(false);
    }

    /// <summary>Прежний ModConfig — в архив в хранилище профиля, затем файлы из пака поверх.</summary>
    private static void ApplyModConfig(PackFile pack, string configDir, ModBackupStore backups)
    {
        if (Directory.Exists(configDir) && Directory.EnumerateFileSystemEntries(configDir).Any())
        {
            var dir = System.IO.Path.Combine(backups.Root, "_ModConfig");
            Directory.CreateDirectory(dir);
            ZipFile.CreateFromDirectory(configDir, System.IO.Path.Combine(dir, $"ModConfig-{DateTime.Now:yyyyMMdd-HHmmss}.zip"));
        }

        var root = System.IO.Path.GetFullPath(configDir) + System.IO.Path.DirectorySeparatorChar;
        foreach (var entry in pack.ConfigEntries)
        {
            var rel = entry.FullName[PackManifest.ConfigFolder.Length..];
            var dest = System.IO.Path.GetFullPath(System.IO.Path.Combine(configDir, rel));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // защита от ../ в чужом архиве
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }
}

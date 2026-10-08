using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.Core.Server;

/// <summary>Мод на сервере — путь на той машине (для окна это просто имя), modinfo или ошибка чтения.</summary>
public sealed record RemoteMod(string Path, ModInfo? Info, string? Error);

/// <summary>Моды сервера для окна на другой машине: что стоит, что выключено, куда ставить новые и какая версия игры.</summary>
public sealed record RemoteModList(string? GameVersion, IReadOnlyList<string> DisabledMods, IReadOnlyList<string> ModDirs,
    string? InstallDir, IReadOnlyList<RemoteMod> Mods, DateTime? ChangedAt);

public sealed record ModToggleRequest(string Path, bool Enabled);
public sealed record ModPathRequest(string Path);

/// <summary>Итог обновления модов перед перезапуском: что обновлено («имя: было → стало»), что пропущено и почему, что не вышло.</summary>
public sealed record ModAutoUpdateResult(IReadOnlyList<string> Updated, IReadOnlyList<string> Skipped, IReadOnlyList<string> Failed);

/// <summary>Итог установки: «имя: было → стало» или «имя версия».</summary>
public sealed record ModInstallResult(string Name, string? Version, string? OldVersion);

/// <summary>
/// Моды серверного профиля на этой машине — для удалённого окна (им пользуется агент). Логика та же, что у вкладки
/// «Мои моды»: папки из конфига, выключение через WorldConfig.DisabledMods, заменённые версии — в хранилище для отката.
/// </summary>
public sealed class ServerMods(string profileId, string gameDir, string dataDir)
{
    private GameProfile Profile => new() { Id = profileId, Name = profileId, Kind = ProfileKind.Server, GameDir = gameDir, DataDir = dataDir };

    public ResolvedProfile Resolve() => ProfileResolver.Resolve(Profile);

    /// <summary>Моды сервера (с разбором modinfo) — для поиска мода-виновника падения.</summary>
    public IReadOnlyList<LocalMod> Locals() => ModUpdateService.ScanLocal(Resolve());

    public RemoteModList List()
    {
        var resolved = Resolve();
        return new RemoteModList(resolved.GameVersion?.ToString(), resolved.DisabledMods, resolved.ModDirs, resolved.InstallDir,
            [.. ModUpdateService.ScanLocal(resolved).Select(m => new RemoteMod(m.Path, m.Info, m.Error))], StampOf(resolved));
    }

    /// <summary>Мод по пути — только из списка установленных: чужой путь сюда не пройдёт.</summary>
    private static LocalMod Find(ResolvedProfile resolved, string path) =>
        ModUpdateService.ScanLocal(resolved).FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase))
        ?? throw new FileNotFoundException(Loc.T("mods.remoteNotFound", System.IO.Path.GetFileName(path)));

    public void SetEnabled(string path, bool enabled)
    {
        var resolved = Resolve();
        var mod = Find(resolved, path);
        if (mod.Info is null) throw new InvalidOperationException(mod.Error ?? path);
        ModConfigEditor.SetEnabled(resolved, mod.Info, enabled);
    }

    public void Delete(string path)
    {
        var resolved = Resolve();
        var mod = Find(resolved, path);
        RecycleBin.Send(mod.Path);
        // других копий мода не осталось — убираем его и из списка выключенных
        if (mod.Info is { } info && ModUpdateService.ScanLocal(resolved).All(l => l.Info?.ModId != info.ModId))
            ModConfigEditor.Forget(resolved, info);
    }

    /// <summary>Поставить присланный архив: заменённые версии — в хранилище для отката, выключенный мод остаётся выключенным.</summary>
    public ModInstallResult Install(string zipPath)
    {
        var resolved = Resolve();
        var plan = ModInstaller.Plan(zipPath, resolved, ModUpdateService.ScanLocal(resolved));
        var info = plan.Incoming.Info!;
        var disabled = new HashSet<string>(resolved.DisabledMods);
        var wasDisabled = plan.Replaces.Any(r => ModUpdateService.IsDisabled(r, disabled));
        ModInstaller.Apply(plan, new ModBackupStore(System.IO.Path.Combine(AppPaths.ModBackups, profileId)));
        if (wasDisabled) ModConfigEditor.SetEnabled(Resolve(), info, enabled: false);
        return new ModInstallResult(info.Name, info.Version, plan.Replaces.FirstOrDefault()?.Info?.Version);
    }

    /// <summary>
    /// Обновить моды сервера (перезапуск по расписанию, сервер остановлен): модбаза → у каких модов есть релиз новее
    /// под версию игры сервера → скачать и поставить, прежние версии — в хранилище для отката, выключенные остаются
    /// выключенными. Закреплённые моды и пропущенные версии не трогаются (<paramref name="policy"/>). Релиз, которому
    /// нужна игра новее, не ставится. Сбой одного мода не мешает остальным; модбаза недоступна — исключение, ничего не тронуто.
    /// </summary>
    public async Task<ModAutoUpdateResult> UpdateAllAsync(ModPolicy policy, bool allowUnstable, Mods.ModUpdater updater,
        ModDb.ModDbClient db, CancellationToken ct = default)
    {
        var resolved = Resolve();
        var game = resolved.GameVersion ?? throw new InvalidOperationException(Loc.T("err.noGameVersion"));
        var locals = ModUpdateService.ScanLocal(resolved);
        var remote = await new ModUpdateService(db).FetchRemoteAsync(locals, ct: ct).ConfigureAwait(false);
        var due = UpdateChecker.Evaluate(locals, remote, game, allowUnstable, policy)
            .Where(r => r.Status == ModStatus.UpdateAvailable && r.LatestCompatible?.MainFile is not null && r.Local.Info is not null)
            .ToList();

        var updated = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>();
        foreach (var r in due)
        {
            var info = r.Local.Info!;
            var release = r.LatestCompatible!;
            var name = string.IsNullOrWhiteSpace(info.Name) ? info.ModId : info.Name;
            string? file = null;
            try
            {
                file = await updater.DownloadReleaseAsync(release, info.ModId, ct: ct).ConfigureAwait(false);
                // модбаза помечает релизы веткой игры; точное требование — только в самом файле
                if (ModInstaller.Plan(file, Resolve(), ModUpdateService.ScanLocal(Resolve())).NeedsGame is { } need)
                {
                    skipped.Add(Loc.T("autoupd.needsGame", name, release.ModVersion, need));
                    continue;
                }
                var result = Install(file);
                updated.Add($"{name}: {result.OldVersion ?? info.Version} → {result.Version}");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
                                           or HttpRequestException or UnauthorizedAccessException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                failed.Add($"{name} {release.ModVersion}: {ex.Message}");
            }
            finally
            {
                if (file is not null) updater.Cleanup(file);
            }
        }
        return new ModAutoUpdateResult(updated, skipped, failed);
    }

    /// <summary>Когда последний раз менялись моды: папки модов (файл добавили, заменили, убрали) и конфиг (включили/выключили).</summary>
    public DateTime? ChangedAt()
    {
        var resolved = Resolve();
        return StampOf(resolved);
    }

    /// <summary>Отметка набора модов профиля: самое позднее время изменения его папок модов и файла настроек.</summary>
    public static DateTime? StampOf(ResolvedProfile resolved)
    {
        DateTime? latest = null;
        foreach (var dir in resolved.ModDirs.Where(Directory.Exists))
        {
            var t = Directory.GetLastWriteTimeUtc(dir);
            if (latest is null || t > latest) latest = t;
        }
        if (resolved.ConfigPath is { } config && File.Exists(config))
        {
            var t = File.GetLastWriteTimeUtc(config);
            if (latest is null || t > latest) latest = t;
        }
        return latest;
    }
}

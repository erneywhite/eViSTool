using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.Core.Hosting;

public sealed partial class HostedMods
{
    /// <summary>
    /// Поставить архив (уже скачанный на эту машину) на сервер у хостера. Порядок — «залить → удалить старое →
    /// переименовать»: многие серверы не переименовывают поверх, а две версии одного мода в папке ломают старт сервера.
    /// <list type="number">
    /// <item>скан и план по тем же правилам, что у своего профиля (имена — по листингу);</item>
    /// <item><paramref name="confirm"/> — вопросы окна (та же версия, даунгрейд); отказ — null, ничего не тронуто;</item>
    /// <item>перепроверка по свежему листингу: имя свободно, заменяемые файлы те же;</item>
    /// <item>копии заменяемых версий — на эту машину (сбой — на сервере ничего не тронуто);</item>
    /// <item>архив заливается как «имя.evistool.tmp» (сервер такой файл модом не считает), размер сверяется;</item>
    /// <item>журнал установки, потом удаление старых версий и переименование; сбой — откат по копиям, а если сорвался и
    /// откат, журнал доведёт следующий скан;</item>
    /// <item>копии — в хранилище версий (для отката), изменение — в историю; выключенный мод остаётся выключенным.</item>
    /// </list>
    /// </summary>
    public Task<ModInstallOutcome?> InstallAsync(string zip, Func<InstallPlan, bool>? confirm, CancellationToken ct) =>
        HostedSessions.UseAsync(Id, Hosted.Endpoint, files => InstallCoreAsync(files, zip, confirm, ct), ct);

    private async Task<ModInstallOutcome?> InstallCoreAsync(IRemoteFiles files, string zip, Func<InstallPlan, bool>? confirm,
        CancellationToken ct)
    {
        var scan = await ScanCoreAsync(files, null, ct).ConfigureAwait(false);
        // прерванную установку скан довести не смог — новая затёрла бы её журнал, и копию старой версии никто бы не вернул
        if (HostedJournal.Read(Id) is { } stuck)
            throw new IOException(Loc.T("hosted.errPendingInstall", stuck.Name ?? stuck.ModId));
        var listing = new List<RemoteEntry>();
        foreach (var dir in scan.Profile.ModDirs)
            listing.AddRange(await scan.Snapshot.Listings.ListAsync(dir, ct).ConfigureAwait(false) ?? []);

        var plan = ModInstaller.Plan(zip, scan.Profile, scan.Mods, new RemoteModPaths(listing));
        foreach (var old in plan.Replaces) EnsureWritable(scan, old);
        if (confirm is not null && !confirm(plan)) return null;
        var info = plan.Incoming.Info!;
        var targetDir = RemotePath.Parent(plan.TargetPath);

        // ---- перепроверка по свежему листингу: пока человек отвечал на вопросы, файлы могли поменять
        var fresh = new HostedListings(files, Root);
        var replaced = plan.Replaces.Select(r => r.Path).ToHashSet(StringComparer.Ordinal);
        if (await fresh.EntryAsync(plan.TargetPath, ct).ConfigureAwait(false) is not null && !replaced.Contains(plan.TargetPath))
            throw new IOException(Loc.T("err.installTargetTaken", RemotePath.Name(plan.TargetPath)));
        foreach (var old in plan.Replaces)
        {
            var then = scan.Entries[old.Path];
            if (await fresh.FileAsync(old.Path, ct).ConfigureAwait(false) is not { } now || now.Size != then.Size || now.Modified != then.Modified)
                throw new IOException(Loc.T("err.installTargetTaken", RemotePath.Name(old.Path)));
        }

        // ---- копии заменяемых версий на эту машину
        var oldDir = HostedJournal.OldDirFor(Id);
        var removed = new List<HostedPendingOld>();
        try
        {
            Directory.CreateDirectory(oldDir);
            foreach (var old in plan.Replaces)
            {
                var name = RemotePath.SafeLocalName(RemotePath.Name(old.Path));
                var local = Path.Combine(oldDir, name);
                if (File.Exists(local)) local = Path.Combine(oldDir, $"{removed.Count}-{name}"); // то же имя в другой папке модов
                await using (var file = File.Create(local))
                    await files.DownloadAsync(old.Path, file, null, ct).ConfigureAwait(false);
                removed.Add(new HostedPendingOld(old.Path, local, scan.Entries[old.Path].Size));
            }
        }
        catch
        {
            DeleteLocalDir(oldDir);
            throw;
        }

        // ---- архив — временным файлом рядом
        var tmp = plan.TargetPath + ".evistool.tmp";
        var size = new FileInfo(zip).Length;
        try
        {
            await using (var source = File.OpenRead(zip))
                await files.UploadAsync(source, tmp, null, ct).ConfigureAwait(false);
            fresh.Forget(targetDir);
            if (await fresh.FileAsync(tmp, ct).ConfigureAwait(false) is not { } uploaded || uploaded.Size != size)
                throw new RemoteFilesException(RemoteFailure.Protocol, $"{RemotePath.Name(tmp)}: the uploaded size differs");
        }
        catch
        {
            await QuietlyAsync(() => files.DeleteFileAsync(tmp, CancellationToken.None)).ConfigureAwait(false);
            DeleteLocalDir(oldDir);
            throw;
        }

        // ---- журнал, удалить старое, переименовать новое
        var pending = new HostedPending(tmp, plan.TargetPath, size, removed, info.ModId, info.Name,
            plan.Replaces.FirstOrDefault()?.Info?.Version, info.Version);
        try
        {
            HostedJournal.Write(Id, pending);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // без журнала дальше не идём: прерванную установку потом было бы не довести
            await QuietlyAsync(() => files.DeleteFileAsync(tmp, CancellationToken.None)).ConfigureAwait(false);
            DeleteLocalDir(oldDir);
            throw;
        }
        try
        {
            foreach (var old in removed) await files.DeleteFileAsync(old.Path, ct).ConfigureAwait(false);
            await files.RenameAsync(tmp, plan.TargetPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            var inPlace = false;
            try
            {
                // false — переименование на самом деле прошло (потерялся только ответ): откатывать нечего
                if (await HostedJournal.RollBackAsync(files, pending, CancellationToken.None, Root).ConfigureAwait(false))
                    HostedJournal.Delete(Id, pending);
                else inPlace = true;
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
            {
                // откат не вышел (связь оборвалась) — журнал остаётся, его доведёт следующий скан
            }
            if (!inPlace) throw;
        }

        // ---- готово: копии — в хранилище версий, изменение — в историю, архив — в кэш modinfo
        HostedJournal.KeepOld(Id, pending);
        HostedJournal.Delete(Id, pending);
        fresh.Forget(targetDir);
        if (await fresh.FileAsync(plan.TargetPath, ct).ConfigureAwait(false) is { } placed)
        {
            var cache = ModInfoCache.ForProfile(Id);
            await using (var source = File.OpenRead(zip)) cache.Put(placed, ZipProbe.ReadZip(source));
            cache.Save();
        }

        // выключенный мод остаётся выключенным (запись «modid@версия» старой версии новую не выключила бы)
        var disabled = new HashSet<string>(scan.Profile.DisabledMods);
        if (plan.Replaces.Any(r => ModUpdateService.IsDisabled(r, disabled)) && scan.Profile.ConfigPath is { } config)
            await EditConfigAsync(files, config, root => ModConfigEditor.SetEnabled(root, ProfileKind.Server, info, enabled: false), ct)
                .ConfigureAwait(false);

        return new ModInstallOutcome(info.Name, info.Version, plan.Replaces.FirstOrDefault()?.Info?.Version);
    }
}

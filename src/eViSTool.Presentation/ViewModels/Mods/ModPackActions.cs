using System.IO;
using System.Net.Http;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Packs;

namespace eViSTool.Presentation.ViewModels.Mods;

/// <summary>
/// Модпаки вкладки «Мои моды»: собрать .evpack из модов своего профиля и поставить модпак в открытый профиль — свой
/// или удалённый сервер. Поставленные моды, нужные и в связанных профилях (сервер ↔ клиент), предлагаются туда же.
/// </summary>
internal sealed class ModPackActions(ModsViewModel owner, ModInstallService installer, ModUpdateService service,
    ModDbClient db, ModUpdater updater)
{
    /// <summary>Версия программы без хвоста сборки — в «создано в eViSTool …» модпака.</summary>
    public static string AppVersion =>
        typeof(ModsViewModel).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "";

    public async Task ExportAsync()
    {
        if (owner.Profile is not { } profile || owner.IsBusy || !owner.IsLocalProfile) return;
        var enabled = owner.Locals.Count(l => l.Info is not null && !l.IsDisabled);
        var disabled = owner.Locals.Count(l => l.Info is not null && l.IsDisabled);
        if (enabled + disabled == 0)
        {
            Error(Loc.T("packx.noMods"));
            return;
        }

        var hasConfig = profile.Profile.DataDir is { } data && Directory.Exists(Path.Combine(data, "ModConfig"));
        if (AppWindows.Current.ExportPack(profile.Profile.Name, enabled, disabled, hasConfig) is not { } options) return;

        var savePath = Ui.Platform.PickSaveFile(Loc.T("packx.saveTitle"), [new FileFilter(Loc.T("pack.filter"), "*" + PackManifest.Extension)],
            string.Concat(options.Name.Split(Path.GetInvalidFileNameChars())) + PackManifest.Extension);
        if (savePath is null) return;

        owner.IsBusy = true;
        try
        {
            // что есть в модбазе — можно не класть внутрь; для этого нужен ответ модбазы
            if (!options.BundleFiles && owner.ModDbAnswers.Count == 0)
            {
                owner.StatusText = Loc.T("check.querying", 0, owner.Locals.Count);
                var asking = new StatusProgress(s => owner.StatusText = s);
                try { owner.ModDbAnswers = await service.FetchRemoteAsync(owner.Locals, asking); }
                finally { asking.Close(); }
            }
            var onModDb = owner.ModDbAnswers.Where(r => r.Value.Mod is not null).Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            owner.StatusText = Loc.T("packx.building");
            var locals = owner.Locals;
            var manifest = await Task.Run(() => PackBuilder.Build(profile, locals, options, onModDb, savePath, $"eViSTool {AppVersion}"));
            var size = new FileInfo(savePath).Length / 1024.0 / 1024.0;
            owner.StatusText = Loc.T("packx.done", manifest.Mods.Count, size.ToString("0.0"), savePath);
            Ui.ShowInFolder(savePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            Error(Loc.T("packx.failed", ex.Message));
        }
        finally
        {
            owner.IsBusy = false;
        }
    }

    public async Task ImportFileAsync(string path)
    {
        if (owner.Profile is not { } profile || owner.IsBusy || owner.ChangeTarget() is not { } target) return;

        PackFile pack;
        try
        {
            pack = PackFile.Open(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Newtonsoft.Json.JsonException)
        {
            Error(Loc.T("packi.openFailed", ex.Message));
            return;
        }

        using (pack)
        {
            // что стоит в цели: своя папка или сервер на другой машине (список — со слов его агента)
            PackImportPlan plan;
            try
            {
                var (resolved, locals) = await ModTargets.ScanAsync(target);
                plan = PackImporter.Plan(pack.Manifest, resolved, locals);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or IOException)
            {
                Error(Loc.T("mods.readFailed", ex.Message));
                return;
            }
            if (AppWindows.Current.ConfirmPackImport(plan) is not { } packChoice) return;
            if (!await installer.ConfirmIfRunningAsync(target)) return;

            owner.IsBusy = true;
            PackImportResult result;
            var copyProblems = new List<string>();
            // поставленные моды — копиями сюда: потом предложим «Установить также в» связанные профили
            var keep = Path.Combine(eViSTool.Core.AppPaths.Downloads, "pack-also-" + Guid.NewGuid().ToString("N")[..8]);
            var alsoDone = new List<string>();
            var alsoProblems = new List<string>();
            try
            {
                await installer.CopyWorldsAsync(target, copyProblems);
                var importer = new PackImporter(db, updater) { KeepInstalledIn = keep };
                var progress = new StatusProgress(s => owner.StatusText = s);
                var (mirror, applyConfig) = (packChoice.Mirror, packChoice.ApplyConfig);
                // в фоне: иначе при паке «всё внутри» импорт идёт синхронно, окно подвисает,
                // а сообщения прогресса приходят уже после итога и затирают его
                // файлы с модбазы, перезалитые авторами модов, — ставить ли (спрашиваем до любых изменений профиля)
                bool AskChanged(IReadOnlyList<string> mods)
                {
                    var yes = false;
                    Ui.Invoke(() => yes = Ui.Confirm(Loc.T("pack.askChanged", string.Join("\n", mods.Select(x => "• " + x))), byDefault: Answer.No, warning: true));
                    return yes;
                }
                // не своя машина (агент, хостер) — моды по одному через цель; своя — прямо в папки профиля
                result = await Task.Run(() => target.IsLocal
                    ? importer.ApplyAsync(pack, plan, profile, mirror, applyConfig,
                        ModBackupStore.ForProfile(profile.Profile), progress, confirmChanged: AskChanged)
                    : importer.ApplyToTargetAsync(pack, plan, target, mirror, applyConfig, progress, confirmChanged: AskChanged));
                progress.Close(); // отчёты, ещё не дошедшие из очереди, не затрут итог
                // поставленное на сервер у хостера — в оповещение «Обновлены моды» (уйдёт с итогом операции)
                owner.HostedWatch?.ModsChanged(target, result.Done, []);

                // моды пака, нужные и в других профилях (сервер ↔ клиент), — как у «Добавить»
                var zips = Directory.Exists(keep) ? Directory.GetFiles(keep, "*.zip", SearchOption.AllDirectories) : [];
                if (zips.Length > 0 && await installer.PickAlsoForFilesAsync(target, zips) is { Count: > 0 } also)
                    foreach (var a in await installer.ConfirmedAsync(also, x => x.Target))
                        foreach (var (zipPath, _) in a.ToInstall)
                            await ModInstallService.Labelled(a.Target, label: true, alsoDone, alsoProblems,
                                () => installer.InstallZipAsync(a.Target, zipPath, interactive: false, alsoDone, alsoProblems));
            }
            finally
            {
                owner.IsBusy = false;
                try { if (Directory.Exists(keep)) Directory.Delete(keep, recursive: true); } catch (IOException) { }
            }
            await owner.ReportAsync(Loc.T("report.imported", pack.Manifest.Name), [.. result.Done, .. alsoDone],
                [.. copyProblems, .. result.Problems, .. alsoProblems]);
        }
    }

    private static void Error(string text) =>
        Ui.Warn(text);
}

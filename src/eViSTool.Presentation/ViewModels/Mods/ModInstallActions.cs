using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Packs;
using eViSTool.Core.Versioning;

namespace eViSTool.Presentation.ViewModels.Mods;

/// <summary>
/// Установка сразу, мимо очереди: архивы («Добавить» и перетаскивание), релиз из каталога, доустановка зависимостей.
/// На время установки вкладка занята; моды, нужные и в связанных профилях (сервер ↔ клиент), можно поставить и туда.
/// Итог — строкой статуса, а что требует внимания — отдельным окном.
/// </summary>
internal sealed class ModInstallActions(ModsViewModel owner, ModInstallService installer, ModPackActions packs)
{
    /// <summary>Установка набора zip (кнопка или перетаскивание). Модпак среди файлов — ставится он, как «Импорт».</summary>
    public async Task AddFilesAsync(IEnumerable<string> files)
    {
        if (owner.Profile is null || owner.IsBusy) return;

        var list = files.ToList();
        if (list.FirstOrDefault(f => f.EndsWith(PackManifest.Extension, StringComparison.OrdinalIgnoreCase)) is { } pack)
        {
            _ = packs.ImportFileAsync(pack);
            return;
        }
        var zips = list.Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        if (zips.Count == 0)
        {
            Error(Loc.T("mods.notZip"));
            return;
        }

        if (owner.CurrentTarget() is not { } target) return;

        var installed = new List<string>();
        var problems = new List<string>();
        // занято — сразу, до первого await: второе быстрое перетаскивание, пока идёт проверка сервера у хостера или выбор
        // «поставить также в», иначе прошло бы проверку выше и запустило вторую установку
        owner.IsBusy = true;
        try
        {
            if (!await installer.ConfirmIfRunningAsync(target)) return;
            // моды, нужные и в других профилях (сервер ↔ клиент), — предложить поставить и туда
            var also = await installer.PickAlsoForFilesAsync(target, zips);
            if (also is null) return; // отменили
            also = await installer.ConfirmedAsync(also, a => a.Target);

            using var history = ModHistory.Begin(ModHistorySource.Zip);
            foreach (var zip in zips)
                await installer.InstallZipAsync(target, zip, interactive: true, installed, problems);
            foreach (var a in also)
                foreach (var (path, _) in a.ToInstall)
                    await ModInstallService.Labelled(a.Target, label: true, installed, problems,
                        () => installer.InstallZipAsync(a.Target, path, interactive: false, installed, problems));
        }
        finally
        {
            owner.IsBusy = false;
        }

        await owner.ReloadAsync();
        if (installed.Count > 0) owner.StatusText = Loc.T("report.installed") + ": " + string.Join("; ", installed);

        if (problems.Count > 0)
        {
            var text = (installed.Count > 0 ? Loc.T("report.installed") + ":\n• " + string.Join("\n• ", installed) + "\n\n" : "")
                       + Loc.T("report.attention") + ":\n• " + string.Join("\n• ", problems);
            Ui.Warn(text);
        }
    }

    /// <summary>Докачать недостающие и устаревшие зависимости открытого профиля и включить выключенные — после вопроса.</summary>
    public async Task FixDependenciesAsync()
    {
        if (owner.Profile is not { } start || owner.IsBusy || owner.DependencyIssues.Count == 0 || owner.CurrentTarget() is not { } target) return;
        if (start.GameVersion is not { } game)
        {
            Error(Loc.T("mods.noGameVersion"));
            return;
        }

        var list = string.Join("\n", owner.DependencyIssues.Select(i => "• " + i.Describe()));
        if (!Confirm(Loc.T("mods.fixDepsConfirm", list))) return;
        // занято до первого await: пока идёт проверка сервера у хостера, вторая операция не начнётся
        owner.IsBusy = true;
        var go = false;
        try
        {
            go = await installer.ConfirmIfRunningAsync(target);
        }
        finally
        {
            if (!go) owner.IsBusy = false;
        }
        if (!go) return;

        var installed = new List<string>();
        var problems = new List<string>();
        using var history = ModHistory.Begin(ModHistorySource.Dependencies);
        try
        {
            await installer.FixDependencyIssuesAsync(target, game, installed, problems);
        }
        finally
        {
            owner.IsBusy = false;
        }
        await owner.ReportAsync(Loc.T("report.dependencies"), installed, problems);
    }

    /// <summary>
    /// Скачать и поставить релиз из каталога — в текущий профиль и, если выбрали, ещё в другие (каждому свой релиз),
    /// затем предложить доставить зависимости: в каждом профиле, куда мод встал, — по одному общему вопросу.
    /// </summary>
    public async Task<bool> InstallFromCatalogAsync(string modId, string name, ModDbRelease release,
        IReadOnlyList<(ModTarget Target, ModDbRelease Release)>? also)
    {
        if (owner.Profile is not { } profile || owner.IsBusy || owner.CurrentTarget() is not { } target) return false;
        // занято до первого await: второй щелчок, пока идёт проверка сервера у хостера, не начнёт вторую установку
        owner.IsBusy = true;
        List<(ModTarget Target, ModDbRelease Release)>? extra = null;
        try
        {
            if (await installer.ConfirmIfRunningAsync(target))
                extra = await installer.ConfirmedAsync(also ?? [], a => a.Target); // игра или сервер там запущены — спросить
        }
        finally
        {
            if (extra is null) owner.IsBusy = false; // не ставим — свободно; ставим — занято и дальше, без мигания
        }
        if (extra is null) return false;

        var installed = new List<string>();
        var problems = new List<string>();
        owner.IsBusy = true;
        using var history = ModHistory.Begin(ModHistorySource.Catalog);
        try
        {
            var done = new List<ModTarget>();
            foreach (var (t, r) in extra.Prepend((target, release)))
                if (await ModInstallService.Labelled(t, t != target && extra.Count > 0, installed, problems,
                        () => installer.DownloadAndInstallAsync(t, modId, name, r, installed, problems)))
                    done.Add(t);
            if (done.Count == 0)
            {
                await owner.ReportAsync(Loc.T("report.install"), installed, problems);
                return false;
            }

            // зависимости — у тех целей, куда ставили, даже если профиль успели переключить
            var pending = new List<(ModTarget Target, ModVersion Game, IReadOnlyList<DependencyIssue> Issues)>();
            foreach (var t in done)
            {
                var (resolved, mods) = await ModTargets.ScanAsync(t);
                var issues = Dependencies.FindIssues(mods);
                if (issues.Count > 0 && (resolved.GameVersion ?? (t == target ? profile.GameVersion : null)) is { } game)
                    pending.Add((t, game, issues));
            }
            if (done.Any(installer.IsActive)) await owner.ReloadAsync();
            if (pending.Count > 0)
            {
                var list = pending.Count == 1 && pending[0].Target == target
                    ? string.Join("\n", pending[0].Issues.Select(i => "• " + i.Describe()))
                    : string.Join("\n\n", pending.Select(p => p.Target.Name + ":\n" + string.Join("\n", p.Issues.Select(i => "• " + i.Describe()))));
                if (Confirm(Loc.T("mods.installDepsConfirm", name, list)))
                    foreach (var (t, game, _) in pending)
                        await ModInstallService.Labelled(t, pending.Count > 1 || t != target, installed, problems, async () =>
                        {
                            await installer.FixDependencyIssuesAsync(t, game, installed, problems);
                            return true;
                        });
                if (done.Any(installer.IsActive)) await owner.ReloadAsync();
            }
        }
        finally
        {
            owner.IsBusy = false;
        }
        await owner.ReportAsync(Loc.T("report.installed"), installed, problems);
        return true;
    }

    private static bool Confirm(string text) =>
        Ui.Confirm(text);

    private static void Error(string text) =>
        Ui.Warn(text);
}

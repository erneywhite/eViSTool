using System.IO;
using System.Net.Http;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Versioning;

namespace eViSTool.Presentation.ViewModels.Mods;

/// <summary>
/// Установка модов в цель — общая для всех сценариев вкладки «Мои моды»: очереди, «Добавить», каталога, зависимостей,
/// модпака и отката. Архив с вопросами про ту же версию и даунгрейд, скачивание релиза, доустановка зависимостей,
/// копии миров перед изменением, окно «Установить также в». Состояния вкладки здесь нет: ход работы уходит в строку
/// статуса (<c>setStatus</c>), итоги — в списки installed и problems, которые собирает вызывающий.
/// </summary>
/// <param name="gameVersion">Версия игры открытого профиля — для подписи «этот профиль» в окне «Установить также в».</param>
/// <param name="now">Часы для памяти ответа «сервер остановлен» (тесты подменяют).</param>
internal sealed class ModInstallService(MainViewModel main, ModUpdater updater, Action<string> setStatus, Func<ModVersion?> gameVersion,
    Func<DateTime>? now = null)
{
    /// <summary>Вопрос «останови сервер в панели хостера» и память ответа.</summary>
    private readonly HostedStopReminder _stopReminder = new(now);

    /// <summary>Миры, скопированные в текущей операции: попадут в её итоговую строку (<see cref="WithWorldCopies"/>).</summary>
    private readonly List<string> _copiedWorlds = [];

    /// <summary>Цель — открытый сейчас профиль.</summary>
    public bool IsActive(ModTarget target) => target.ProfileId == main.ActiveProfile?.Model.Id;

    /// <summary>
    /// Игра или сервер цели запущены (свой — по процессу, удалённый — со слов агента, если это активный профиль).
    /// У хостера по FTP это не видно — всегда false, вместо этого <see cref="ConfirmIfRunning"/> спрашивает человека.
    /// </summary>
    public bool IsRunning(ModTarget target) =>
        !target.IsHosted && (target.IsRemote ? IsActive(target) && main.Server.IsServerUp : GameProcess.IsRunning(target.Profile));

    /// <summary>
    /// Игра или сервер цели запущены — спросить, продолжать ли. true — не запущены или ответили «да». У хостера вопрос
    /// другой: «останови сервер в панели хостера» (Да / Нет, текст — <paramref name="hostedQuestion"/>, если задан);
    /// «Да» помнится 10 минут, флажок профиля выключает вопрос.
    /// </summary>
    public bool ConfirmIfRunning(ModTarget target, string? hostedQuestion = null) =>
        target.IsHosted ? _stopReminder.Confirm(target, hostedQuestion)
        : !IsRunning(target)
          || Confirm(Loc.T("mods.runningContinue"));

    /// <summary>Итог операции + «сделана копия мира: …», если была; список копий после этого сбрасывается.</summary>
    public string WithWorldCopies(string text)
    {
        if (_copiedWorlds.Count == 0) return text;
        var note = Loc.T("wcopy.done", string.Join(", ", _copiedWorlds));
        _copiedWorlds.Clear();
        return string.IsNullOrEmpty(text) ? note : text + " · " + note;
    }

    /// <summary>
    /// Перед изменением модов игрового профиля — копии одиночных миров, в которые играли с прошлой копии (настройка).
    /// Повторный вызов в той же операции ничего не копирует: миры с тех пор не менялись. Сбой копии изменение
    /// не останавливает — уходит в problems (нет списка — в строку статуса). Свои серверы и удалённые — не трогаем:
    /// у них свои копии мира по расписанию.
    /// </summary>
    public async Task CopyWorldsAsync(ModTarget target, List<string>? problems = null, ModSide side = ModSide.Both)
    {
        // скопированные миры — в итоговую строку статуса операции (иначе её сразу затрёт «мод выключен» и т. п.)
        if (!main.CopyWorldsBeforeModChanges || !target.IsLocal || target.Profile.Kind != ProfileKind.Client
            || !NeedsWorldCopy(side)) return;
        var data = string.IsNullOrWhiteSpace(target.Profile.DataDir) ? GameInstall.DefaultDataDir : target.Profile.DataDir;
        if (!WorldCopies.AnyToCopy(data)) return;
        void Note(string text)
        {
            if (problems is null) setStatus(text);
            else if (!problems.Contains(text)) problems.Add(text);
        }
        // игра пишет в мир прямо сейчас — цельной копии не снять
        if (GameProcess.IsRunning(target.Profile))
        {
            Note(Loc.T("wcopy.gameRunning"));
            return;
        }
        setStatus(Loc.T("wcopy.copying"));
        var (copied, failed) = await Task.Run(() => WorldCopies.Refresh(data));
        _copiedWorlds.AddRange(copied.Where(c => !_copiedWorlds.Contains(c)));
        if (copied.Count > 0) main.RefreshWorldCopies(target.Profile);
        setStatus("");
        foreach (var f in failed) Note(f);
    }

    /// <summary>
    /// Нужна ли копия миров перед изменением мода этой стороны. Клиентский мод (шейдеры, интерфейс) в одиночной игре
    /// работает только в клиентской части и мир не меняет — копию не делаем. Не указано (несколько модов сразу,
    /// пак, откат сеанса) — как у игры, «обе стороны»: копируем.
    /// </summary>
    public static bool NeedsWorldCopy(ModSide side) => side != ModSide.Client;

    /// <summary>
    /// Ставит один zip в профиль: старая версия — в хранилище, выключенный мод остаётся выключенным.
    /// interactive — спрашивать про ту же версию и даунгрейд (при обновлении/откате не спрашиваем: решение уже принято).
    /// </summary>
    public async Task<bool> InstallZipAsync(ModTarget target, string zip, bool interactive, List<string> installed, List<string> problems)
    {
        await CopyWorldsAsync(target, problems, ModSides.Parse(ModScanner.ReadZip(zip).Info?.Side));
        try
        {
            // ставится строго в цель: свою папку или удалённый сервер (своё соединение), активный профиль не важен.
            // План приходит из фонового потока (после сканирования), а вопросы — окна: спрашиваем в потоке окна
            var outcome = await ModTargets.InstallAsync(target, zip, plan =>
            {
                var go = false;
                Ui.Invoke(() =>
                {
                    var info = plan.Incoming.Info!;
                    var old = plan.Replaces.FirstOrDefault()?.Info?.Version;
                    if (interactive && plan.IsSameVersion && !Confirm(Loc.T("mods.reinstallConfirm", info.Name, info.Version))) return;
                    if (interactive && plan.IsDowngrade && !Confirm(Loc.T("mods.downgradeConfirm", info.Name, old, info.Version))) return;
                    // мод требует игру новее — спрашиваем всегда, и при обновлении: модбаза помечает релизы веткой (1.22.x),
                    // а точное требование видно только в самом файле
                    if (plan.NeedsGame is { } need)
                    {
                        var question = old is null
                            ? Loc.T("mods.needsGameConfirm", info.Name, info.Version, need, target.Profile.Name, plan.Game)
                            : Loc.T("mods.needsGameUpdateConfirm", info.Name, info.Version, need, target.Profile.Name, plan.Game, old);
                        if (!Confirm(question))
                        {
                            problems.Add(Loc.T("mods.needsGameSkipped", info.Name, info.Version, need, target.Profile.Name));
                            return;
                        }
                    }
                    if (!target.IsLocal) setStatus(Loc.T("mods.uploading", info.Name, info.Version));
                    go = true;
                });
                return go;
            });
            if (outcome is null) return false;
            installed.Add(outcome.Text);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException
                                       or HttpRequestException or TaskCanceledException)
        {
            problems.Add($"{Path.GetFileName(zip)}: {(target.IsHosted ? ModTargets.Describe(target, ex) : ex.Message)}");
            return false;
        }
    }

    /// <summary>Скачать релиз и поставить его. Ошибки — в problems.</summary>
    public async Task<bool> DownloadAndInstallAsync(ModTarget target, string modId, string name, ModDbRelease release,
        List<string> installed, List<string> problems, Action<double>? onProgress = null, CancellationToken ct = default)
    {
        string? file = null;
        try
        {
            var progress = new Progress<double>(x =>
            {
                setStatus(Loc.T("dl.progress", name, release.ModVersion, x.ToString("P0")));
                onProgress?.Invoke(x);
            });
            file = await updater.DownloadReleaseAsync(release, modId, progress, ct);
            return await InstallZipAsync(target, file, interactive: false, installed, problems);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            problems.Add(Loc.T("dl.failed", name, release.ModVersion, ex.Message));
            return false;
        }
        finally
        {
            if (file is not null) updater.Cleanup(file);
        }
    }

    /// <summary>
    /// Докачивает недостающие/устаревшие зависимости и включает выключенные. Несколько кругов:
    /// у зависимостей бывают свои зависимости. Без вопросов — вызывающий уже спросил.
    /// </summary>
    public async Task FixDependencyIssuesAsync(ModTarget target, ModVersion game, List<string> installed, List<string> problems)
    {
        await CopyWorldsAsync(target, problems); // включение выключенных идёт мимо установки
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var round = 0; round < 5; round++)
            {
                // проблемы — по модам самой цели, а не того профиля, что открыт сейчас
                var (_, mods) = await ModTargets.ScanAsync(target);
                var issues = Dependencies.FindIssues(mods).Where(i => tried.Add(i.ModId)).ToList();
                if (issues.Count == 0) break;

                foreach (var issue in issues)
                {
                    if (issue.IsDisabled)
                    {
                        var mod = mods.First(l => string.Equals(l.Info?.ModId, issue.ModId, StringComparison.OrdinalIgnoreCase));
                        await ModTargets.SetEnabledAsync(target, mod, enabled: true);
                        installed.Add(Loc.T("mods.enabledShort", mod.Info!.Name));
                        continue;
                    }

                    setStatus(Loc.T("mods.searchingModDb", issue.ModId));
                    var found = await updater.FindBestReleaseAsync(issue.ModId, game, main.AllowUnstable, target.Profile.ToPolicy());
                    if (found is not { } f)
                    {
                        problems.Add(Loc.T("mods.depNotFound", issue.ModId, $"{game.Major}.{game.Minor}.x"));
                        continue;
                    }
                    await DownloadAndInstallAsync(target, issue.ModId, f.Mod.Name ?? issue.ModId, f.Release, installed, problems);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException
                                       or UnauthorizedAccessException)
        {
            problems.Add(ex.Message);
        }
    }

    /// <summary>Сделать шаг и, если ставим в несколько профилей, подписать его строки отчёта профилем: «Carry On 1.14 → Client C».</summary>
    public static async Task<bool> Labelled(ModTarget target, bool label, List<string> installed, List<string> problems, Func<Task<bool>> step)
    {
        var (i0, p0) = (installed.Count, problems.Count);
        var ok = await step();
        if (label)
        {
            for (var i = i0; i < installed.Count; i++) installed[i] += " → " + target.Name;
            for (var i = p0; i < problems.Count; i++) problems[i] = target.Name + ": " + problems[i];
        }
        return ok;
    }

    /// <summary>Окно «Установить также в» для добавленных файлов. Пустой список — только сюда, null — отмена.</summary>
    public async Task<IReadOnlyList<AlsoInstallFiles>?> PickAlsoForFilesAsync(ModTarget target, IReadOnlyList<string> zips)
    {
        if (main.ActiveProfile is not { } active) return [];
        var files = zips.Select(z => (Path: z, ModScanner.ReadZip(z).Info))
            .Where(f => f.Info is not null && AlsoInstall.WorthOffering(ModSides.Parse(f.Info.Side)))
            .Select(f => (f.Path, f.Info!)).ToList();
        if (files.Count == 0) return [];
        var others = main.Profiles
            .Where(p => p != active && files.Any(f => AlsoInstall.Needed(ModSides.Parse(f.Item2.Side), p.Model.Kind)))
            .Select(p => (Profile: p, Target: ModTarget.For(p.Model)))
            .Where(x => x.Target is not null)
            .ToList();
        if (others.Count == 0) return [];

        setStatus(Loc.T("also.checking"));
        using var cts = new CancellationTokenSource(others.Max(x => ModTargets.ScanTimeout(x.Target!))); // сервер у хостера читается дольше
        var options = await Task.WhenAll(others.Select(x => AlsoInstall.EvaluateFilesAsync(x.Target!, files, cts.Token)));
        setStatus("");

        var names = string.Join(", ", files.Select(f => f.Item2.Name));
        var chosen = AlsoInstallPicker.Choose(main, active, Loc.T("also.hintFiles", names),
            Loc.T("also.thisProfile") + " · " + AlsoInstall.GameText(gameVersion()),
            [.. options.Select((o, i) => new AlsoChoice(others[i].Profile, o, o.Describe(), o.CanInstall,
                o.Error is null && o.ToInstall.Count == 0, active.Model.IsLinkedTo(others[i].Profile.Model)))]);
        return chosen?.Cast<AlsoInstallFiles>().ToList();
    }

    private static bool Confirm(string text) =>
        Ui.Confirm(text);
}

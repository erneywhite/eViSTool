using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Пункт «Скопировать в профиль ▸»: профиль и его вид («Сервер · сервер»).</summary>
public sealed record CopyTargetChoice(string Label, ProfileViewModel Profile, IRelayCommand Command);

/// <summary>
/// Несколько выбранных модов (Ctrl/Shift+щелчок в таблице): справа вместо карточки — панель «Выбрано: N» с действиями
/// сразу над всеми: включить, выключить, обновить, закрепить, категория, скопировать в другой профиль, модпак из
/// выбранных, удалить. Выбор держит окно (у таблицы выбор нескольких строк не привязывается) и сообщает его сюда.
/// </summary>
public sealed partial class ModsViewModel
{
    /// <summary>Выбранные строки таблицы (одна или больше); пусто — ничего не выбрано.</summary>
    public IReadOnlyList<ModRowViewModel> SelectedRows { get; private set; } = [];

    /// <summary>Выбрано больше одного мода — панель «Выбрано: N» вместо карточки.</summary>
    public bool IsMulti => SelectedRows.Count > 1;

    /// <summary>Окно сообщает выбор таблицы.</summary>
    public void SetSelection(IEnumerable<ModRowViewModel> rows)
    {
        var list = rows.Distinct().ToList();
        if (list.Count == SelectedRows.Count && list.All(SelectedRows.Contains)) return;
        var wasMulti = IsMulti;
        SelectedRows = list;
        OnPropertyChanged(nameof(SelectedRows));
        if (wasMulti != IsMulti) OnPropertyChanged(nameof(IsMulti));
        NotifyMulti();
    }

    private void NotifyMulti()
    {
        foreach (var name in new[] { nameof(MultiTitle), nameof(MultiStateText), nameof(MultiSideText), nameof(MultiUpdateText),
                     nameof(MultiCanUpdate), nameof(MultiPinText), nameof(CopyTargets), nameof(HasCopyTargets) })
            OnPropertyChanged(name);
        RefreshCategoryLists(); // в меню «Категория» отмечается общая категория выбранных
    }

    /// <summary>Строки, которые в профиле можно менять (у хостера — не его собственные моды).</summary>
    private List<ModRowViewModel> Changeable => [.. SelectedRows.Where(r => r.Local.Info is not null && HostReadOnlyReason(r) is null)];

    /// <summary>«Выбрано: 7 модов».</summary>
    public string MultiTitle => Loc.T("multi.title", SelectedRows.Count, Loc.Plural("multi.mods", SelectedRows.Count));

    /// <summary>«включено 5 · выключено 2».</summary>
    public string MultiStateText => Loc.T("multi.state", SelectedRows.Count(r => r.IsEnabled), SelectedRows.Count(r => !r.IsEnabled));

    /// <summary>«клиентских 2 · общих 4 · серверных 1» — по стороне мода.</summary>
    public string MultiSideText
    {
        get
        {
            var sides = SelectedRows.Select(r => ModSides.Parse(r.Local.Info?.Side)).ToList();
            return Loc.T("multi.sides", sides.Count(s => s == ModSide.Client), sides.Count(s => s == ModSide.Both), sides.Count(s => s == ModSide.Server));
        }
    }

    public bool MultiCanUpdate => SelectedRows.Any(r => r.CanUpdate);

    /// <summary>«Обновить (3)».</summary>
    public string MultiUpdateText => Loc.T("multi.update", SelectedRows.Count(r => r.CanUpdate));

    /// <summary>Все выбранные закреплены — кнопка снимает закрепление, иначе — закрепляет.</summary>
    private bool AllPinned => SelectedRows.Count > 0 && SelectedRows.All(r => r.IsPinned);

    public string MultiPinText => Loc.T(AllPinned ? "multi.unpin" : "multi.pin");

    // ---------- включить и выключить ----------

    [RelayCommand]
    private Task EnableSelectedAsync() => SetEnabledSelectedAsync(true);

    [RelayCommand]
    private Task DisableSelectedAsync() => SetEnabledSelectedAsync(false);

    /// <summary>
    /// Включить или выключить выбранные: один вопрос (если игра или сервер запущены), одна копия миров, потом по одному
    /// моду — в той же очереди, что и щелчки по переключателям.
    /// </summary>
    private async Task SetEnabledSelectedAsync(bool enabled)
    {
        var rows = Changeable.Where(r => r.IsEnabled != enabled).ToList();
        if (IsBusy || rows.Count == 0 || CurrentTarget() is not { } target) return;
        if (target.IsHosted)
        {
            if (RejectWorldJob(target) || !await _installer.ConfirmIfRunningAsync(target)) return;
        }
        else if (_installer.IsRunning(target))
        {
            var what = Loc.T(target.Profile.Kind == ProfileKind.Server ? "mods.toggleServerRunning" : "mods.toggleGameRunning");
            if (!Confirm(Loc.T(enabled ? "multi.enableAnyway" : "multi.disableAnyway", what, rows.Count))) return;
        }

        // занято на всю операцию: иначе слежение за папкой (у хостера — опрос по FTP) замечало наши же изменения и
        // перечитывало моды посреди операции, а очередь обновлений могла взяться за те же файлы
        var owner = _main.ActiveProfile;
        IsBusy = true;
        await _toggleGate.WaitAsync();
        var failed = new List<string>();
        try
        {
            if (_main.ActiveProfile != owner) return;
            // копия миров одна на всех: нужна, если среди модов есть не только клиентские
            var side = rows.All(r => ModSides.Parse(r.Local.Info!.Side) == ModSide.Client) ? ModSide.Client : ModSide.Both;
            await _installer.CopyWorldsAsync(target, side: side);
            foreach (var row in rows)
            {
                _renew.Add(row.FilePath);
                try
                {
                    await ModTargets.SetEnabledAsync(target, row.Local, enabled);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
                                               or HttpRequestException or TaskCanceledException)
                {
                    failed.Add($"{row.Name}: {ErrorText(target, ex)}");
                }
            }
        }
        finally
        {
            _toggleGate.Release();
            IsBusy = false;
        }
        StatusText = WithStartHint(_installer.WithWorldCopies(Loc.T(enabled ? "multi.enabledOk" : "multi.disabledOk", rows.Count - failed.Count)),
            target.IsHosted);
        if (failed.Count > 0) Error(Loc.T("mods.toggleFailed", string.Join("\n", failed)));
        await ReloadAsync();
    }

    // ---------- обновить, закрепить ----------

    /// <summary>Обновить выбранные, у которых есть обновление, — общей очередью, как «Обновить всё».</summary>
    [RelayCommand]
    private void UpdateSelected()
    {
        var todo = SelectedRows.Where(r => r.CanUpdate && HostReadOnlyReason(r) is null).ToList();
        if (todo.Count == 0 || CurrentTarget() is not { } target) return;
        Enqueue(todo.Select(r => QueueItemFor(target, r, r.Result.LatestCompatible!)), ModHistorySource.Update);
    }

    /// <summary>Закрепить выбранные (на их версиях); если все уже закреплены — снять закрепление.</summary>
    [RelayCommand]
    private void PinSelected()
    {
        if (_main.ActiveProfile is not { } profile || SelectedRows.Count == 0) return;
        var pins = profile.Model.PinnedMods;
        var unpin = AllPinned;
        foreach (var row in SelectedRows.Where(r => r.Local.Info is not null))
        {
            if (unpin) pins.Remove(row.Local.Info!.ModId);
            else pins[row.Local.Info!.ModId] = row.Local.Info.Version ?? "";
        }
        _main.SaveSettings();
        StatusText = Loc.T(unpin ? "multi.unpinnedOk" : "multi.pinnedOk", SelectedRows.Count);
        Rebuild();
    }

    // ---------- удалить ----------

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var rows = Changeable;
        if (IsBusy || rows.Count == 0 || Profile is null || ChangeTarget() is not { } target) return;
        if (target.IsHosted && RejectWorldJob(target)) return; // идёт копия мира у хостера — соединение занято ею
        var list = string.Join("\n", rows.Take(15).Select(r => $"• {r.Name} {r.Installed}")) + (rows.Count > 15 ? "\n" + Loc.T("multi.andMore", rows.Count - 15) : "");
        var question = Loc.T(target.IsHosted ? "multi.deleteConfirmHosted" : "multi.deleteConfirm", rows.Count, list)
                       + (!target.IsHosted && _installer.IsRunning(target) ? "\n\n" + Loc.T("mods.deleteRunning") : "");
        if (!Confirm(question)) return;
        if (target.IsHosted && !await _installer.ConfirmIfRunningAsync(target)) return;

        // занято и под той же очередью, что переключатели: см. SetEnabledSelectedAsync
        var owner = _main.ActiveProfile;
        IsBusy = true;
        await _toggleGate.WaitAsync();
        var failed = new List<string>();
        try
        {
            if (_main.ActiveProfile != owner) return;
            var side = rows.All(r => ModSides.Parse(r.Local.Info!.Side) == ModSide.Client) ? ModSide.Client : ModSide.Both;
            await _installer.CopyWorldsAsync(target, side: side);
            using (ModHistory.Begin(ModHistorySource.Manual)) // все удаления — одно действие в истории
                foreach (var row in rows)
                    try
                    {
                        await ModTargets.DeleteAsync(target, row.Local, Ui.Platform.MoveToTrash);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException
                                                   or HttpRequestException or InvalidOperationException)
                    {
                        failed.Add(Loc.T("mods.deleteFailed", row.Name, ErrorText(target, ex)));
                    }
        }
        finally
        {
            _toggleGate.Release();
            IsBusy = false;
        }
        StatusText = WithStartHint(_installer.WithWorldCopies(Loc.T("multi.deletedOk", rows.Count - failed.Count)), target.IsHosted);
        if (failed.Count > 0) Error(string.Join("\n", failed));
        await ReloadAsync();
    }

    // ---------- скопировать в профиль ----------

    /// <summary>
    /// Куда можно скопировать выбранные: остальные профили. Файлы модов берутся с этой машины, поэтому копировать
    /// можно только из своего профиля (не с удалённого сервера и не от хостера).
    /// </summary>
    public IReadOnlyList<CopyTargetChoice> CopyTargets => !IsLocalProfile || _main.ActiveProfile is not { } active ? []
        : [.. _main.Profiles.Where(p => p != active)
            .Select(p => new CopyTargetChoice($"{p.Name} · {p.KindText}", p, new AsyncRelayCommand(() => CopySelectedToAsync(p))))];

    public bool HasCopyTargets => CopyTargets.Count > 0;

    /// <summary>
    /// Поставить выбранные моды в другой профиль — те же файлы, через обычную установку (старые версии там уходят в
    /// хранилище). В серверный профиль клиентские моды не ставятся: выделенный сервер их не загружает.
    /// </summary>
    internal async Task CopySelectedToAsync(ProfileViewModel to)
    {
        if (IsBusy || !IsLocalProfile) return;
        var rows = SelectedRows.Where(r => r.Local.Info is not null).ToList();
        if (rows.Count == 0) return;
        if (ModTarget.For(to.Model) is not { } target)
        {
            Error(ModTarget.UnreadableText(to.Model));
            return;
        }
        var skippedClient = to.Model.Kind == ProfileKind.Server
            ? rows.Where(r => ModSides.IsUnneeded(ModSides.Parse(r.Local.Info!.Side), ProfileKind.Server)).ToList() : [];
        var folders = rows.Where(r => Directory.Exists(r.FilePath)).ToList(); // мод папкой — не архив, поставить нечем
        var todo = rows.Except(skippedClient).Except(folders).ToList();

        var lines = new List<string>();
        if (skippedClient.Count > 0) lines.Add(Loc.T("multi.copyClientSkipped", skippedClient.Count, string.Join(", ", skippedClient.Select(r => r.Name))));
        if (folders.Count > 0) lines.Add(Loc.T("multi.copyFolders", string.Join(", ", folders.Select(r => r.Name))));
        if (todo.Count == 0)
        {
            Error(Loc.T("multi.copyNothing", to.Name) + (lines.Count > 0 ? "\n\n" + string.Join("\n", lines) : ""));
            return;
        }
        var list = string.Join("\n", todo.Take(15).Select(r => $"• {r.Name} {r.Installed}")) + (todo.Count > 15 ? "\n" + Loc.T("multi.andMore", todo.Count - 15) : "");
        if (!Confirm(Loc.T("multi.copyConfirm", todo.Count, to.Name, list) + (lines.Count > 0 ? "\n\n" + string.Join("\n", lines) : ""))) return;
        if (!await _installer.ConfirmIfRunningAsync(target)) return;

        IsBusy = true;
        var done = new List<string>();
        var problems = new List<string>();
        try
        {
            using (ModHistory.Begin(ModHistorySource.Manual))
                foreach (var row in todo)
                    await _installer.InstallZipAsync(target, row.FilePath, interactive: false, done, problems);
        }
        finally
        {
            IsBusy = false;
        }
        // не ошибка, а решение: в итог, что поставлено
        if (skippedClient.Count > 0) done.Add(lines[0]);
        await ReportAsync(Loc.T("multi.copyReport", to.Name), done, problems);
    }

    // ---------- модпак из выбранных ----------

    /// <summary>«Модпак из выбранных ▸ Файлом…» — .evpack только из выбранных модов (файлы с этой машины).</summary>
    [RelayCommand]
    private Task ExportSelectedAsync() =>
        _packs.ExportAsync([.. SelectedRows.Select(r => r.Local).Where(l => l.Info is not null)]);

    /// <summary>«Модпак из выбранных ▸ Кодом» — код набора из выбранных модов в буфер (работает и для сервера не на этой машине).</summary>
    [RelayCommand]
    private Task CopySelectedCodeAsync() =>
        _codes.CopyAsync([.. SelectedRows.Select(r => r.Local).Where(l => l.Info is not null)]);
}

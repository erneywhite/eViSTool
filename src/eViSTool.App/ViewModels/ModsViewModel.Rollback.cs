using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;

namespace eViSTool.App.ViewModels;

/// <summary>Откат из вкладки «История»: сеанс целиком или один мод — к версиям, какие были до сеанса.</summary>
public sealed partial class ModsViewModel
{
    /// <summary>
    /// Что сделать с модом при откате: поставить прежнюю версию (из сохранённой копии или релизом модбазы), убрать
    /// поставленный в сеансе — или ничего, если вернуть нечем (<see cref="Problem"/>).
    /// </summary>
    private sealed record RevertStep(HistoryChangeRow Change, LocalMod? Current, string? BackupPath, ModDbRelease? Release,
        bool Remove, string? Problem);

    [RelayCommand]
    private Task RollbackSessionAsync(HistorySessionRow? session) =>
        session is null ? Task.CompletedTask : RevertAsync(session, [.. session.Changes.Where(c => c.CanRevert)]);

    [RelayCommand]
    private Task RevertChangeAsync(HistoryChangeRow? change) =>
        change is null || History.Selected is not { } session ? Task.CompletedTask : RevertAsync(session, [change]);

    private async Task RevertAsync(HistorySessionRow session, IReadOnlyList<HistoryChangeRow> changes)
    {
        if ((IsBusy && !IsQueueRunning) || CurrentTarget() is not { } target) return;
        if (changes.Count == 0)
        {
            StatusText = Loc.T("hist.nothingToRevert");
            return;
        }

        StatusText = Loc.T("hist.preparing");
        var steps = new List<RevertStep>();
        foreach (var change in changes)
            steps.Add(await PlanRevertAsync(target, change));
        StatusText = "";

        var doable = steps.Where(s => s.Problem is null).ToList();
        var lines = string.Join("\n", steps.Select(Describe));
        if (doable.Count == 0)
        {
            Error(Loc.T("hist.allProblems", lines));
            return;
        }
        if (!Confirm(Loc.T("hist.confirm", session.When, lines)) || !ConfirmIfRunning(target)) return;

        // откат — одно действие в истории, со ссылкой на сеанс, который он откатывает
        var history = ModHistory.NewScope(ModHistorySource.Rollback, session.Session.Id);
        await CopyWorldsAsync(target);
        var problems = new List<string>();
        using (ModHistory.Join(history.Id, history.Source, history.Undoes))
            foreach (var step in doable.Where(s => s.Remove))
                await RemoveForRevertAsync(target, step, problems);
        if (doable.Any(s => s.Remove)) await ReloadAsync();

        var installs = doable.Where(s => !s.Remove)
            .Select(s => new UpdateQueueItem(target, s.Change.ModId, s.Change.Title, s.Current?.Info?.Version ?? "",
                s.Change.Change.From!, s.Release, s.BackupPath) { History = history })
            .ToList();
        if (installs.Count > 0) Enqueue(installs, history.Source, history.Undoes);
        StatusText = problems.Count > 0 ? string.Join("; ", problems) : Loc.T("hist.revertStarted");
    }

    /// <summary>Где взять прежнюю версию мода: сохранённая копия этого профиля, иначе релиз модбазы с той же версией.</summary>
    private async Task<RevertStep> PlanRevertAsync(ModTarget target, HistoryChangeRow change)
    {
        var current = _locals.FirstOrDefault(l => string.Equals(l.Info?.ModId, change.ModId, StringComparison.OrdinalIgnoreCase));
        var from = change.Change.From;
        if (from is null) return new RevertStep(change, current, null, null, Remove: true, current is null ? Loc.T("hist.alreadyGone") : null);

        // прежние копии удалённого сервера лежат на той машине — отсюда берём релиз модбазы
        if (!target.IsRemote)
            foreach (var path in ModBackupStore.ForProfile(target.Profile).List(change.ModId))
                if (File.Exists(path) && string.Equals(ModScanner.ReadZip(path).Info?.Version, from, StringComparison.OrdinalIgnoreCase))
                    return new RevertStep(change, current, path, null, Remove: false, null);
        try
        {
            var mod = await _db.GetModAsync(change.ModId);
            var release = mod?.Releases.FirstOrDefault(r => r.MainFile is not null
                && string.Equals(r.ModVersion, from, StringComparison.OrdinalIgnoreCase));
            return release is not null
                ? new RevertStep(change, current, null, release, Remove: false, null)
                : new RevertStep(change, current, null, null, Remove: false, Loc.T("hist.noSuchVersion", from));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new RevertStep(change, current, null, null, Remove: false, Loc.T("hist.noModDb", from, ex.Message));
        }
    }

    private static string Describe(RevertStep s)
    {
        var now = s.Current?.Info?.Version ?? "—";
        if (s.Problem is not null) return Loc.T("hist.stepProblem", s.Change.Title, s.Problem);
        if (s.Remove) return Loc.T("hist.stepRemove", s.Change.Title, now);
        return Loc.T(s.BackupPath is not null ? "hist.stepFromCopy" : "hist.stepFromModDb", s.Change.Title, now, s.Change.Change.From);
    }

    /// <summary>Убрать мод, поставленный в откатываемом сеансе: в корзину (у сервера — в его), с записью в историю.</summary>
    private async Task RemoveForRevertAsync(ModTarget target, RevertStep step, List<string> problems)
    {
        var mod = step.Current!;
        try
        {
            if (target.Remote is { } code)
            {
                using var agent = AgentClient.ForRemote(code);
                await agent.DeleteModAsync(mod.Path);
                return;
            }
            Shell.MoveToRecycleBin(mod.Path);
            if (mod.Info is { } info)
            {
                ModHistory.Record(ModHistory.FileFor(target.ProfileId), info.ModId, info.Name, info.Version, null);
                if (_locals.Count(l => l.Info?.ModId == info.ModId) == 1 && ProfileResolver.Resolve(target.Profile) is { } resolved)
                    ModConfigEditor.Forget(resolved, info);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException
                                       or OperationCanceledException)
        {
            problems.Add(Loc.T("hist.removeFailed", step.Change.Title, ex.Message));
        }
    }
}

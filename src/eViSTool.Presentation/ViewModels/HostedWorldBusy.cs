using System.ComponentModel;
using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Операция с копией мира у открытого профиля у хостера (<see cref="HostedWorldJobs"/>) — для разделов, которым она
/// занимает соединение профиля на всю передачу: «Обзор» раздела «Сервер» и «Мои моды». Пока она идёт, их запросы к
/// серверу молча ждали бы её конца, поэтому они в сеть не ходят, а показывают строку <see cref="Text"/>: что идёт, как
/// далеко и где отменить. Кончилась — <see cref="BusyChanged"/>, и раздел продолжает сам. Всё — в потоке окна.
/// </summary>
/// <param name="jobs">Операции окна; null — их нет (тесты раздела без главной модели).</param>
internal sealed class HostedWorldBusy(Func<HostedWorldJobs?> jobs)
{
    private HostedWorldJobs? _watched; // на чьи события подписаны
    private string? _profileId;
    private HostedWorldJob? _job;

    /// <summary>Операция профиля началась или кончилась (сменился <see cref="IsBusy"/>).</summary>
    public event Action? BusyChanged;

    /// <summary>Строка <see cref="Text"/> поменялась: ход операции, её начало или конец.</summary>
    public event Action? TextChanged;

    /// <summary>У профиля идёт операция с копией мира: в его сеть сейчас не ходить.</summary>
    public bool IsBusy => _job is not null;

    /// <summary>«Идёт копия мира (Скачиваю мир: 12 МБ из 90 МБ). Подожди или отмени её во вкладке «Копии».» Не идёт — пусто.</summary>
    public string Text => _job is { } job ? Describe(job) : "";

    /// <summary>
    /// Чей профиль смотрит раздел (null — не у хостера). Событий не шлёт: сменив профиль, раздел и так показывает всё
    /// заново и берёт <see cref="IsBusy"/> и <see cref="Text"/> сам.
    /// </summary>
    public void SetProfile(string? profileId)
    {
        Subscribe();
        _profileId = profileId;
        Track(profileId is null ? null : _watched?.Running(profileId));
    }

    /// <summary>Что идёт и где отменить — на текущем языке.</summary>
    public static string Describe(HostedWorldJob job) => job.Kind == HostedWorldJobKind.Restore
        ? Loc.T("hostedbk.busyRestoreHere", HostedWorldText.Progress(job))
        : Loc.T("hostedbk.busyHere", HostedWorldText.Progress(job));

    /// <summary>То же для сервера, который сейчас не открыт: с его именем, чтобы было ясно, о чьей копии речь.</summary>
    public static string Describe(HostedWorldJob job, string server) => job.Kind == HostedWorldJobKind.Restore
        ? Loc.T("hostedbk.busyRestoreOn", server, HostedWorldText.Progress(job))
        : Loc.T("hostedbk.busyOn", server, HostedWorldText.Progress(job));

    // операции окна могут смениться (тест ставит свои) — подписка идёт за ними
    private void Subscribe()
    {
        var current = jobs();
        if (ReferenceEquals(current, _watched)) return;
        if (_watched is { } old)
        {
            old.Started -= OnStarted;
            old.Finished -= OnFinished;
        }
        _watched = current;
        if (current is null) return;
        current.Started += OnStarted;
        current.Finished += OnFinished;
    }

    private void OnStarted(HostedWorldJob job)
    {
        if (job.ProfileId != _profileId) return;
        Track(job);
        TextChanged?.Invoke();
        BusyChanged?.Invoke();
    }

    private void OnFinished(HostedWorldOutcome outcome)
    {
        if (!ReferenceEquals(outcome.Job, _job)) return;
        Track(null);
        TextChanged?.Invoke();
        BusyChanged?.Invoke();
    }

    private void Track(HostedWorldJob? job)
    {
        if (_job is { } old) old.PropertyChanged -= OnJobChanged;
        _job = job;
        if (job is not null) job.PropertyChanged += OnJobChanged;
    }

    private void OnJobChanged(object? sender, PropertyChangedEventArgs e) => TextChanged?.Invoke();
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;

namespace eViSTool.Presentation.ViewModels.Mods;

/// <summary>
/// Что очереди установки нужно от вкладки «Мои моды»: флаг «идёт операция», вопрос про запущенную игру, сама установка
/// пункта, перечитать список после него и итог прогона в строке статуса.
/// </summary>
internal interface IModQueueHost
{
    /// <summary>Идёт операция с модами. Очередь занимает вкладку на время прогона; занято чем-то другим — не добавляет.</summary>
    bool IsBusy { get; set; }

    /// <summary>Итог прогона — в строку статуса вкладки.</summary>
    void SetStatus(string text);

    /// <summary>Цель — открытый сейчас профиль (после её пункта таблицу стоит перечитать сразу).</summary>
    bool IsActive(ModTarget target);

    /// <summary>Игра или сервер цели запущены — спросить, продолжать ли.</summary>
    bool ConfirmIfRunning(ModTarget target);

    /// <summary>Поставить сохранённую копию (без вопросов: решение уже принято). Ошибки — в problems.</summary>
    Task<bool> InstallZipAsync(ModTarget target, string zip, List<string> installed, List<string> problems);

    /// <summary>Скачать релиз модбазы и поставить. Ошибки — в problems.</summary>
    Task<bool> DownloadAndInstallAsync(ModTarget target, string modId, string name, ModDbRelease release,
        List<string> installed, List<string> problems, Action<double> onProgress, CancellationToken ct);

    /// <summary>Перечитать моды открытого профиля и дождаться.</summary>
    Task ReloadAsync();

    /// <summary>Досверить с модбазой моды, появившиеся после прошлой проверки.</summary>
    Task FetchNewModsAsync();
}

/// <summary>
/// Очередь установки вкладки «Мои моды»: обновления, откаты, выбранные версии. Пункты ставятся по одному, каждый в свою
/// цель (запомненную при добавлении), добавлять можно на ходу. Прогон можно остановить, неудачные и отменённые — повторить.
/// </summary>
internal sealed partial class ModInstallQueue(IModQueueHost host) : ObservableObject
{
    private CancellationTokenSource? _cts;

    public ObservableCollection<UpdateQueueItem> Items { get; } = [];

    /// <summary>Идёт прогон: видна «Остановить», не видна «Закрыть».</summary>
    [ObservableProperty] private bool _isRunning;

    public bool HasItems => Items.Count > 0;

    public string Title => Loc.T(IsRunning ? "queue.titleRunning" : "queue.titleDone",
        Items.Count(i => i.State is QueueState.Done or QueueState.Failed or QueueState.Cancelled), Items.Count);

    public double Progress => Items.Count == 0 ? 0
        : (Items.Count(i => i.State is QueueState.Done or QueueState.Failed or QueueState.Cancelled)
           + Items.Where(i => i.IsWorking).Sum(i => i.Progress)) / Items.Count;

    public bool HasRetry => !IsRunning && Items.Any(i => i.State is QueueState.Failed or QueueState.Cancelled);

    /// <summary>Добавлять можно и пока идёт прогон — пункт просто встанет в конец; нельзя, пока вкладка занята другим.</summary>
    public bool CanEnqueue => !host.IsBusy || IsRunning;

    partial void OnIsRunningChanged(bool value)
    {
        // сам флаг уведомляется последним: зависимые от него подписи к этому времени уже пересчитаны
        OnPropertyChanged(nameof(CanEnqueue));
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(HasRetry));
    }

    /// <summary>
    /// Поставить в очередь; уже ждущий или ставящийся мод той же цели второй раз не добавляется.
    /// У каждого пункта своя цель: пункты разных профилей ставятся каждый в свой.
    /// </summary>
    public void Enqueue(IEnumerable<UpdateQueueItem> items, string source, string? undoes = null, bool runningConfirmed = false)
    {
        if (host.IsBusy && !IsRunning) return; // идёт другая операция (импорт модпака и т.п.)
        var adding = items.ToList();
        // всё, что добавлено одним нажатием, — одно действие в истории модов
        var history = ModHistory.NewScope(source, undoes);
        foreach (var item in adding) item.History ??= history;
        // игра или сервер цели запущены — спросить один раз на цель, пока ничего не начато
        // (откат уже спросил сам — второй раз не спрашиваем)
        var declined = runningConfirmed ? [] : adding.Select(i => i.Target).DistinctBy(t => t.ProfileId)
            .Where(t => !host.ConfirmIfRunning(t)).Select(t => t.ProfileId).ToHashSet();
        if (!IsRunning)
        {
            // прошлый прогон закончен — его итоги убираем
            foreach (var old in Items.Where(i => i.State is QueueState.Done).ToList()) Items.Remove(old);
        }
        foreach (var item in adding.Where(i => !declined.Contains(i.Target.ProfileId)))
        {
            var same = Items.FirstOrDefault(i => i.Target.ProfileId == item.Target.ProfileId
                                                 && string.Equals(i.ModId, item.ModId, StringComparison.OrdinalIgnoreCase));
            if (same is { State: QueueState.Waiting or QueueState.Working }) continue;
            if (same is not null) Items.Remove(same);
            Items.Add(item);
        }
        // пункты для разных профилей — у каждого видно, куда он ставится
        var several = Items.Select(i => i.Target.ProfileId).Distinct().Count() > 1;
        foreach (var i in Items) i.ShowTarget = several;
        Notify();
        if (!IsRunning && Items.Any(i => i.State == QueueState.Waiting)) _ = RunAsync();
    }

    private async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        host.IsBusy = true;
        IsRunning = true;
        var installed = new List<string>();
        try
        {
            while (!ct.IsCancellationRequested && Items.FirstOrDefault(i => i.State == QueueState.Waiting) is { } item)
            {
                item.State = QueueState.Working;
                item.Progress = 0;
                Notify();
                var problems = new List<string>();
                var ok = false;
                using var history = ModHistory.Join(item.History?.Id, item.History?.Source, item.History?.Undoes);
                try
                {
                    // каждый пункт — в свою цель, запомненную при добавлении
                    if (item.Path is not null)
                        ok = await host.InstallZipAsync(item.Target, item.Path, installed, problems);
                    else
                        ok = await host.DownloadAndInstallAsync(item.Target, item.ModId, item.Name, item.Release!, installed, problems,
                            x => { item.Progress = x; OnPropertyChanged(nameof(Progress)); }, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // «Отменить» посреди пункта — он станет «отменён» ниже
                }
                catch (Exception ex)
                {
                    // неожиданная ошибка не должна оставлять пункт «в работе» навсегда и останавливать очередь молча:
                    // пункт — «не удалось» с текстом ошибки, подробности — в журнал
                    Ui.Platform.WriteCrashLog(ex);
                    problems.Add(Loc.T("dl.failed", item.Name, item.To, ex.Message));
                }

                item.State = ok ? QueueState.Done : ct.IsCancellationRequested ? QueueState.Cancelled : QueueState.Failed;
                item.Message = ok ? "" : ct.IsCancellationRequested ? "" : problems.FirstOrDefault() ?? "";
                if (ok && host.IsActive(item.Target)) await host.ReloadAsync(); // таблица и карточка сразу показывают новую версию
                Notify();
            }
            foreach (var i in Items.Where(i => i.State == QueueState.Waiting)) i.State = QueueState.Cancelled;
        }
        finally
        {
            IsRunning = false;
            host.IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }

        await host.ReloadAsync();
        _ = host.FetchNewModsAsync();
        var done = Items.Count(i => i.State == QueueState.Done);
        var failed = Items.Count(i => i.State == QueueState.Failed);
        host.SetStatus(Loc.T("queue.finished", done, failed));
    }

    /// <summary>Остановить прогон: текущий пункт прерывается, ждущие станут «отменены».</summary>
    public void Stop() => _cts?.Cancel();

    /// <summary>Поставить заново неудачные и отменённые пункты прошедшего прогона.</summary>
    public void Retry()
    {
        if (IsRunning) return;
        // повтор — в ту же цель, что была у пункта
        foreach (var i in Items.Where(i => i.State is QueueState.Failed or QueueState.Cancelled))
        {
            i.Message = "";
            i.State = QueueState.Waiting;
        }
        Notify();
        _ = RunAsync();
    }

    /// <summary>Убрать прошедший прогон с экрана.</summary>
    public void Close()
    {
        if (IsRunning) return;
        Items.Clear();
        Notify();
    }
}

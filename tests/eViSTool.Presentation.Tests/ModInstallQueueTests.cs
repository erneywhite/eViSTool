using System.Runtime.CompilerServices;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;
using eViSTool.Presentation.ViewModels.Mods;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Очередь установки «Моих модов»: пункты ставятся по одному, каждый в свою цель; повторы, отказ из-за запущенной игры,
/// остановка, повтор неудачных, итог прогона. Вкладка подменена: установка — сценарием теста, без сети и диска.
/// </summary>
public sealed class ModInstallQueueTests
{
    private static readonly ModTarget A = new(new GameProfile { Id = "prof-a", Name = "Клиент A", Kind = ProfileKind.Client }, null);
    private static readonly ModTarget B = new(new GameProfile { Id = "prof-b", Name = "Сервер B", Kind = ProfileKind.Server }, null);

    private static UpdateQueueItem Item(ModTarget target, string modId, string? path = null) =>
        new(target, modId, modId + " mod", "1.0.0", "2.0.0", path is null ? new ModDbRelease { ModVersion = "2.0.0" } : null, path);

    /// <summary>Вкладка для очереди: что ставили и куда, что спрашивали, сколько раз перечитывали.</summary>
    private sealed class Host : IModQueueHost
    {
        public bool IsBusy { get; set; }
        public string? ActiveProfileId { get; set; } = A.ProfileId;

        /// <summary>Цели, где «игра запущена», и ответ пользователя на вопрос «продолжить?».</summary>
        public HashSet<string> Running { get; } = [];
        public bool ContinueIfRunning { get; set; }
        public List<string> AskedFor { get; } = [];

        /// <summary>Как ставится пункт (по modid); нет сценария — удачно.</summary>
        public Dictionary<string, Func<List<string>, CancellationToken, Task<bool>>> Scripts { get; } = [];

        public List<string> Installed { get; } = [];
        public List<string> Statuses { get; } = [];
        public int Reloads { get; private set; }
        public int Fetches { get; private set; }

        public void SetStatus(string text) => Statuses.Add(text);
        public bool IsActive(ModTarget target) => target.ProfileId == ActiveProfileId;

        public bool ConfirmIfRunning(ModTarget target)
        {
            if (!Running.Contains(target.ProfileId)) return true;
            AskedFor.Add(target.ProfileId);
            return ContinueIfRunning;
        }

        public Task<bool> InstallZipAsync(ModTarget target, string zip, List<string> installed, List<string> problems) =>
            Install(target, Path.GetFileNameWithoutExtension(zip), installed, problems, CancellationToken.None);

        public Task<bool> DownloadAndInstallAsync(ModTarget target, string modId, string name, ModDbRelease release,
            List<string> installed, List<string> problems, Action<double> onProgress, CancellationToken ct)
        {
            onProgress(0.5);
            return Install(target, modId, installed, problems, ct);
        }

        private async Task<bool> Install(ModTarget target, string modId, List<string> installed, List<string> problems, CancellationToken ct)
        {
            var ok = Scripts.TryGetValue(modId, out var script) ? await script(problems, ct) : true;
            if (ok)
            {
                Installed.Add($"{modId} → {target.Name}");
                installed.Add(modId);
            }
            return ok;
        }

        public Task ReloadAsync()
        {
            Reloads++;
            return Task.CompletedTask;
        }

        public Task FetchNewModsAsync()
        {
            Fetches++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Run_InstallsEachItemIntoItsOwnTarget_ThenReports() => Sta.Run(() =>
    {
        var host = new Host();
        var queue = new ModInstallQueue(host);

        queue.Enqueue([Item(A, "alpha"), Item(B, "beta"), Item(A, "gamma", path: "/backups/gamma.zip")], ModHistorySource.UpdateAll);
        Sta.WaitFor(() => !queue.IsRunning);

        Assert.Equal(["alpha → Клиент A", "beta → Сервер B", "gamma → Клиент A"], host.Installed);
        Assert.All(queue.Items, i => Assert.Equal(QueueState.Done, i.State));
        // пункты для разных профилей — у каждого видно, куда он ставится
        Assert.All(queue.Items, i => Assert.True(i.ShowTarget));
        // таблица перечитывается после пунктов открытого профиля и в конце прогона; новые моды досверяются с модбазой
        Assert.Equal(3, host.Reloads);
        Assert.Equal(1, host.Fetches);
        Assert.False(host.IsBusy);
        Assert.Equal(Loc.T("queue.finished", 3, 0), host.Statuses.Last());
        Assert.Equal(1.0, queue.Progress);
        Assert.False(queue.HasRetry);
    });

    // всё, что добавлено одним нажатием, — одно действие в истории модов
    [Fact]
    public void OnePress_OneHistoryScope() => Sta.Run(() =>
    {
        var queue = new ModInstallQueue(new Host());
        var items = new[] { Item(A, "alpha"), Item(A, "beta") };

        queue.Enqueue(items, ModHistorySource.UpdateAll);
        Sta.WaitFor(() => !queue.IsRunning);

        Assert.NotNull(items[0].History);
        Assert.Same(items[0].History, items[1].History);
        Assert.Equal(ModHistorySource.UpdateAll, items[0].History!.Source);
    });

    [Fact]
    public void SameModOfSameTarget_NotAddedWhileWaitingOrWorking() => Sta.Run(() =>
    {
        var host = new Host();
        var release = new TaskCompletionSource<bool>();
        host.Scripts["alpha"] = (_, _) => release.Task;
        var queue = new ModInstallQueue(host);

        queue.Enqueue([Item(A, "alpha"), Item(A, "beta")], ModHistorySource.Update);
        Assert.True(queue.IsRunning);
        queue.Enqueue([Item(A, "alpha"), Item(A, "BETA")], ModHistorySource.Update); // ставится и ждёт — второй раз не нужны
        queue.Enqueue([Item(B, "alpha")], ModHistorySource.Update);                  // тот же мод, но в другой профиль

        Assert.Equal(["alpha", "beta", "alpha"], queue.Items.Select(i => i.ModId));
        Assert.Equal([A.ProfileId, A.ProfileId, B.ProfileId], queue.Items.Select(i => i.Target.ProfileId));

        release.SetResult(true);
        Sta.WaitFor(() => !queue.IsRunning);
        Assert.Equal(["alpha → Клиент A", "beta → Клиент A", "alpha → Сервер B"], host.Installed);
    });

    // игра или сервер цели запущены — один вопрос на цель; отказались — пункты этой цели не встают
    [Fact]
    public void RunningTarget_AskedOnce_DeclinedItemsSkipped() => Sta.Run(() =>
    {
        var host = new Host { ContinueIfRunning = false };
        host.Running.Add(B.ProfileId);
        var queue = new ModInstallQueue(host);

        queue.Enqueue([Item(B, "alpha"), Item(A, "beta"), Item(B, "gamma")], ModHistorySource.UpdateAll);
        Sta.WaitFor(() => !queue.IsRunning);

        Assert.Equal([B.ProfileId], host.AskedFor);
        Assert.Equal(["beta → Клиент A"], host.Installed);
        Assert.Equal(["beta"], queue.Items.Select(i => i.ModId));
    });

    // откат уже спросил про запущенную игру сам — второй раз не спрашиваем
    [Fact]
    public void RunningConfirmed_NotAskedAgain() => Sta.Run(() =>
    {
        var host = new Host();
        host.Running.Add(A.ProfileId);
        var queue = new ModInstallQueue(host);

        queue.Enqueue([Item(A, "alpha")], ModHistorySource.Rollback, undoes: "abc123", runningConfirmed: true);
        Sta.WaitFor(() => !queue.IsRunning);

        Assert.Empty(host.AskedFor);
        Assert.Equal(["alpha → Клиент A"], host.Installed);
    });

    // вкладка занята другим (импорт модпака) — в очередь не добавляется; кнопки «Обновить» недоступны
    [Fact]
    public void BusyWithOtherOperation_NothingAdded() => Sta.Run(() =>
    {
        var host = new Host { IsBusy = true };
        var queue = new ModInstallQueue(host);

        Assert.False(queue.CanEnqueue);
        queue.Enqueue([Item(A, "alpha")], ModHistorySource.Update);

        Assert.Empty(queue.Items);
        Assert.Empty(host.Installed);
        Assert.True(host.IsBusy); // чужой флаг очередь не трогает
    });

    [Fact]
    public void WhileRunning_StillCanEnqueue() => Sta.Run(() =>
    {
        var host = new Host();
        var release = new TaskCompletionSource<bool>();
        host.Scripts["alpha"] = (_, _) => release.Task;
        var queue = new ModInstallQueue(host);

        queue.Enqueue([Item(A, "alpha")], ModHistorySource.Update);

        Assert.True(host.IsBusy);
        Assert.True(queue.CanEnqueue); // пункт просто встанет в конец
        release.SetResult(true);
        Sta.WaitFor(() => !queue.IsRunning);
        Assert.False(host.IsBusy);
    });

    [Fact]
    public void Failed_ShowsFirstProblem_RetryPutsItBack() => Sta.Run(() =>
    {
        var host = new Host();
        host.Scripts["alpha"] = (problems, _) =>
        {
            problems.Add("Alpha 2.0.0: нет сети");
            problems.Add("вторая причина");
            return Task.FromResult(false);
        };
        var queue = new ModInstallQueue(host);

        queue.Enqueue([Item(A, "alpha"), Item(A, "beta")], ModHistorySource.UpdateAll);
        Sta.WaitFor(() => !queue.IsRunning);

        var alpha = queue.Items.Single(i => i.ModId == "alpha");
        Assert.Equal(QueueState.Failed, alpha.State);
        Assert.Equal("Alpha 2.0.0: нет сети", alpha.Message);
        Assert.Equal(QueueState.Done, queue.Items.Single(i => i.ModId == "beta").State); // очередь не встала
        Assert.True(queue.HasRetry);
        Assert.Equal(Loc.T("queue.finished", 1, 1), host.Statuses.Last());

        host.Scripts.Remove("alpha"); // сеть появилась
        queue.Retry();
        Sta.WaitFor(() => !queue.IsRunning);

        Assert.Equal(QueueState.Done, alpha.State);
        Assert.Equal("", alpha.Message);
        Assert.False(queue.HasRetry);
        Assert.Equal(["beta → Клиент A", "alpha → Клиент A"], host.Installed);
    });

    [Fact]
    public void Stop_CancelsCurrentAndWaiting() => Sta.Run(() =>
    {
        var host = new Host();
        var started = false;
        host.Scripts["alpha"] = async (_, ct) =>
        {
            started = true;
            await Task.Delay(Timeout.Infinite, ct); // скачивание прерывается отменой
            return true;
        };
        var queue = new ModInstallQueue(host);

        queue.Enqueue([Item(A, "alpha"), Item(A, "beta")], ModHistorySource.UpdateAll);
        Sta.WaitFor(() => started);
        queue.Stop();
        Sta.WaitFor(() => !queue.IsRunning);

        Assert.All(queue.Items, i => Assert.Equal(QueueState.Cancelled, i.State));
        Assert.All(queue.Items, i => Assert.Equal("", i.Message));
        Assert.Empty(host.Installed);
        Assert.True(queue.HasRetry);
        Assert.Equal(Loc.T("queue.finished", 0, 0), host.Statuses.Last());
    });

    // неожиданная ошибка не оставляет пункт «в работе» и не останавливает очередь
    [Fact]
    public void UnexpectedError_ItemFails_QueueGoesOn() => Sta.Run(() =>
    {
        var host = new Host();
        host.Scripts["alpha"] = (_, _) => throw new InvalidOperationException("сломалось");
        var queue = new ModInstallQueue(host);

        queue.Enqueue([Item(A, "alpha"), Item(A, "beta")], ModHistorySource.UpdateAll);
        Sta.WaitFor(() => !queue.IsRunning);

        var alpha = queue.Items.Single(i => i.ModId == "alpha");
        Assert.Equal(QueueState.Failed, alpha.State);
        Assert.Equal(Loc.T("dl.failed", "alpha mod", "2.0.0", "сломалось"), alpha.Message);
        Assert.Equal(QueueState.Done, queue.Items.Single(i => i.ModId == "beta").State);
    });

    // новый прогон убирает поставленные пункты прошлого, неудачные остаются — их можно повторить
    [Fact]
    public void NewRun_DropsDoneItemsOfPreviousRun() => Sta.Run(() =>
    {
        var host = new Host();
        host.Scripts["alpha"] = (_, _) => Task.FromResult(false);
        var queue = new ModInstallQueue(host);
        queue.Enqueue([Item(A, "alpha"), Item(A, "beta")], ModHistorySource.UpdateAll);
        Sta.WaitFor(() => !queue.IsRunning);

        queue.Enqueue([Item(A, "gamma")], ModHistorySource.Update);
        Sta.WaitFor(() => !queue.IsRunning);

        Assert.Equal(["alpha", "gamma"], queue.Items.Select(i => i.ModId));
        Assert.Equal([A.ProfileId, A.ProfileId], queue.Items.Select(i => i.Target.ProfileId));
        Assert.All(queue.Items, i => Assert.False(i.ShowTarget)); // профиль у всех один — подпись не нужна
    });

    [Fact]
    public void Close_ClearsFinishedRun_ButNotARunningOne() => Sta.Run(() =>
    {
        var host = new Host();
        var release = new TaskCompletionSource<bool>();
        host.Scripts["alpha"] = (_, _) => release.Task;
        var queue = new ModInstallQueue(host);

        queue.Enqueue([Item(A, "alpha")], ModHistorySource.Update);
        queue.Close();
        Assert.True(queue.HasItems);

        release.SetResult(true);
        Sta.WaitFor(() => !queue.IsRunning);
        queue.Close();
        Assert.False(queue.HasItems);
    });

    // вкладка отдаёт подписи очереди окну под прежними именами и в прежнем порядке: сам флаг — последним
    [Fact]
    public void Tab_RelaysQueueNotifications_UnderItsOwnNames() => Sta.Run(() =>
    {
        var main = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        var mods = new ModsViewModel(main, new ModDbClient());
        var changed = new List<string?>();
        mods.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        mods.IsQueueRunning = true;

        Assert.Equal([nameof(ModsViewModel.CanQueue), nameof(ModsViewModel.HasQueue), nameof(ModsViewModel.QueueTitle),
            nameof(ModsViewModel.QueueProgress), nameof(ModsViewModel.HasQueueRetry), nameof(ModsViewModel.IsQueueRunning)], changed);
        Assert.True(mods.IsQueueRunning);
        Assert.Equal(Loc.T("queue.titleRunning", 0, 0), mods.QueueTitle);

        changed.Clear();
        mods.IsQueueRunning = false;
        mods.IsBusy = true;

        Assert.Contains(nameof(ModsViewModel.CanQueue), changed);
        Assert.False(mods.CanQueue);
    });
}

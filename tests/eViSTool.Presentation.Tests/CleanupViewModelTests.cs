using eViSTool.Core.Localization;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;
using eViSTool.Core.Profiles;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Окно уборки: общая галочка группы — все / ни одного / часть; выбор пунктов сообщает окну. То, что делал код за окном, — поиск при открытии и проверка раз в 2 секунды, не запущены ли игра и сервер (до закрытия окна), —
/// теперь в модели: таймер ручной, процессы и диск подменены. Уборка — на временной папке, «корзина» её просто удаляет.
/// </summary>
public sealed class CleanupViewModelTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-cleanvm-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly IUiPlatform? _oldPlatform = Ui.IsSet ? Ui.Platform : null;
    private readonly FakePlatform _platform = new();
    private readonly GameProfile _profile;

    // подменённая система
    private bool _running;
    private int _scans;
    private Exception? _scanError;
    private readonly List<string> _trashed = [];
    private readonly HashSet<string> _trashFails = new(StringComparer.OrdinalIgnoreCase);

    public CleanupViewModelTests() =>
        _profile = new GameProfile { Id = "clean-test", Name = "Сервер", Kind = ProfileKind.Server, DataDir = _root };

    public void Dispose()
    {
        Ui.Platform = _oldPlatform!; // как было; null — не задано (иначе заглушка с ручным таймером досталась бы следующим тестам)
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static CleanupItem Item(string title, bool dflt) =>
        new(CleanupKind.ServerMods, title, [title], 1000, DateTime.UtcNow, dflt);

    [Fact]
    public void GroupCheckbox_FollowsItsItems_AndSetsThemAll()
    {
        var changes = 0;
        var group = new CleanupGroupViewModel(
            new CleanupGroup(CleanupKind.ServerMods, CleanupSafety.Safe, [Item("old", true), Item("fresh", false)]), () => changes++);

        Assert.Null(group.IsChecked);              // часть выбрана
        group.IsChecked = true;
        Assert.All(group.Items, i => Assert.True(i.IsChecked));
        Assert.True(group.IsChecked);
        group.Items[0].IsChecked = false;
        Assert.Null(group.IsChecked);
        group.IsChecked = false;
        Assert.False(group.IsChecked);
        Assert.True(changes >= 3);
    }

    // Корзину открывает платформа окна (на Windows — Проводник, на Linux — файловый менеджер), а не сама модель
    [Fact]
    public void OpenRecycleBin_GoesThroughPlatform() => Sta.Run(() =>
    {
        using var script = new ScriptedDialogs();
        new CleanupViewModel(new GameProfile { Id = "clean-test", Name = "clean-test", Kind = ProfileKind.Client }).OpenRecycleBinCommand.Execute(null);
        Assert.Equal(1, script.TrashOpened);
    });

    [Theory]
    [InlineData(0, "0")]
    [InlineData(5_000, "4")]                 // КБ
    [InlineData(250L << 20, "250")]          // МБ
    [InlineData(3L << 30, "3")]              // ГБ
    public void Sizes_AreReadable(long bytes, string number) =>
        Assert.StartsWith(number, WorldCopiesViewModel.SizeText(bytes));

    // ---- открытие и закрытие окна (Loaded, таймер на 2 с и Closed)

    /// <summary>Мусор во временной папке: логи (безопасно, отмечены), карта мира и данные мода (осторожно, не отмечены).</summary>
    private void MakeJunk()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Logs", "Archive"));
        File.WriteAllBytes(Path.Combine(_root, "Logs", "Archive", "server-main.log"), new byte[5000]);
        Directory.CreateDirectory(Path.Combine(_root, "Maps"));
        File.WriteAllBytes(Path.Combine(_root, "Maps", "Дом.db"), new byte[3000]);
        Directory.CreateDirectory(Path.Combine(_root, "ModData", "expandedfoods"));
        File.WriteAllBytes(Path.Combine(_root, "ModData", "expandedfoods", "cache.json"), new byte[2000]);
    }

    /// <summary>Поиск по временной папке: только то, что ещё лежит (после уборки — меньше).</summary>
    private IReadOnlyList<CleanupGroup> Scan(GameProfile profile, string dataDir)
    {
        Interlocked.Increment(ref _scans);
        Assert.Same(_profile, profile);
        Assert.Equal(_root, dataDir);
        if (_scanError is not null) throw _scanError;
        var groups = new List<CleanupGroup>();
        void Add(CleanupKind kind, CleanupSafety safety, string title, string path, bool dflt)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            groups.Add(new CleanupGroup(kind, safety, [new CleanupItem(kind, title, [path], DiskCleanup.SizeOf(path), new DateTime(2026, 10, 1), dflt)]));
        }
        Add(CleanupKind.OldLogs, CleanupSafety.Safe, "Logs\\Archive", Path.Combine(_root, "Logs", "Archive"), true);
        Add(CleanupKind.WorldMaps, CleanupSafety.Careful, "Дом", Path.Combine(_root, "Maps", "Дом.db"), false);
        Add(CleanupKind.RemovedModData, CleanupSafety.Careful, "expandedfoods", Path.Combine(_root, "ModData", "expandedfoods"), false);
        return groups;
    }

    private CleanupViewModel Model() => new(_profile, new CleanupServices
    {
        IsRunning = p =>
        {
            Assert.Same(_profile, p);
            return _running;
        },
        Scan = Scan,
        Trash = path =>
        {
            if (_trashFails.Contains(path)) throw new IOException("файл занят");
            lock (_trashed) _trashed.Add(path);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        },
    });

    [Fact]
    public void Start_ScansAndChecksRunningEveryTwoSeconds_UntilStopped() => Sta.Run(() =>
    {
        Ui.Platform = _platform;
        MakeJunk();
        var vm = Model();
        Assert.Null(_platform.Timer);            // до открытия окна никто не следит

        Sta.Wait(vm.StartAsync());

        var timer = _platform.Timer!;
        Assert.Equal(TimeSpan.FromSeconds(2), timer.Interval);
        Assert.True(timer.IsEnabled);
        Assert.Equal(1, _scans);
        Assert.Equal(["Logs\\Archive", "Дом", "expandedfoods"], vm.Groups.Select(g => g.Items[0].Title));
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsEmpty);
        Assert.Equal("", vm.Status);
        Assert.Equal(Loc.T("clean.summary", WorldCopiesViewModel.SizeText(10_000), WorldCopiesViewModel.SizeText(5000)), vm.Summary);
        Assert.Equal(Loc.T("clean.remove", WorldCopiesViewModel.SizeText(5000)), vm.RemoveText);
        Assert.False(vm.IsRunning);
        Assert.True(vm.RemoveCommand.CanExecute(null));

        // запустили сервер — через 2 секунды кнопка гаснет и видна причина; остановили — оживает сама
        _running = true;
        timer.Fire();
        Assert.True(vm.IsRunning);
        Assert.False(vm.RemoveCommand.CanExecute(null));
        Assert.Equal(Loc.T("clean.serverRunning"), vm.RunningText);
        _running = false;
        timer.Fire();
        Assert.False(vm.IsRunning);
        Assert.True(vm.RemoveCommand.CanExecute(null));

        // окно закрыли — больше не проверяем
        vm.Stop();
        Assert.False(timer.IsEnabled);
        _running = true;
        timer.Fire();
        Assert.False(vm.IsRunning);
        Assert.Equal(1, _scans);
    });

    [Fact]
    public void Start_WhileGameRuns_ShowsItRightAway_AndStopBeforeStartIsHarmless() => Sta.Run(() =>
    {
        Ui.Platform = _platform;
        MakeJunk();
        var client = new CleanupViewModel(new GameProfile { Id = "c", Name = "Клиент", Kind = ProfileKind.Client, DataDir = _root },
            new CleanupServices { IsRunning = _ => true, Scan = (_, _) => [] });
        client.Stop(); // закрыли, не успев открыть, — ничего не падает

        Sta.Wait(client.StartAsync());

        Assert.True(client.IsRunning);           // проверено сразу после поиска, а не через 2 секунды
        Assert.Equal(Loc.T("clean.gameRunning"), client.RunningText);
        Assert.True(client.IsEmpty);
        Assert.False(client.RemoveCommand.CanExecute(null));
    });

    [Fact]
    public void Start_ScanFailure_ShowsReason() => Sta.Run(() =>
    {
        Ui.Platform = _platform;
        _scanError = new UnauthorizedAccessException("нет доступа к ModData");
        var vm = Model();

        Sta.Wait(vm.StartAsync());

        Assert.Equal(Loc.T("clean.scanFailed", "нет доступа к ModData"), vm.Status);
        Assert.False(vm.IsBusy);
        Assert.True(vm.IsEmpty);
        Assert.True(_platform.Timer!.IsEnabled); // за игрой следим всё равно
    });

    // ---- уборка (ветки модели; «корзина» — подменённая, удаляет во временной папке)

    [Fact]
    public void Remove_ConfirmedWithCarefulItems_TrashesChosen_AndRescans() => Sta.Run(() =>
    {
        Ui.Platform = _platform;
        MakeJunk();
        var vm = Model();
        Sta.Wait(vm.StartAsync());
        vm.Groups[1].IsChecked = true;           // карта мира — «осторожно»
        _platform.Answers.Enqueue(Answer.Yes);

        Sta.Wait(vm.RemoveCommand.ExecuteAsync(null));

        var question = Assert.Single(_platform.Asked);
        Assert.StartsWith(Loc.T("clean.confirm", 2, WorldCopiesViewModel.SizeText(8000)), question);
        Assert.Contains(Loc.T("clean.confirmCareful") + "\n• " + Loc.T("clean.kind.WorldMaps"), question);
        Assert.DoesNotContain(Loc.T("clean.kind.OldLogs"), question); // безопасное в список «может пригодиться» не идёт
        Assert.Equal([Path.Combine(_root, "Logs", "Archive"), Path.Combine(_root, "Maps", "Дом.db")], _trashed);
        Assert.True(vm.HasRecycled);
        Assert.Equal(2, _scans);
        Assert.Equal(["expandedfoods"], vm.Groups.Select(g => g.Items[0].Title));
        Assert.Equal(Loc.T("clean.done", WorldCopiesViewModel.SizeText(8000)), vm.Status);
        Assert.False(vm.IsBusy);
        Assert.False(vm.RemoveCommand.CanExecute(null)); // осталось только не отмеченное
    });

    [Fact]
    public void Remove_Declined_TrashesNothing() => Sta.Run(() =>
    {
        Ui.Platform = _platform;
        MakeJunk();
        var vm = Model();
        Sta.Wait(vm.StartAsync());
        _platform.Answers.Enqueue(Answer.No);

        Sta.Wait(vm.RemoveCommand.ExecuteAsync(null));

        var question = Assert.Single(_platform.Asked);
        Assert.Equal(Loc.T("clean.confirm", 1, WorldCopiesViewModel.SizeText(5000)), question); // осторожного не выбрано — без списка
        Assert.Empty(_trashed);
        Assert.False(vm.HasRecycled);
        Assert.Equal(1, _scans);
        Assert.Equal("", vm.Status);
    });

    [Fact]
    public void Remove_GameStartedMeanwhile_WarnsAndKeepsFiles() => Sta.Run(() =>
    {
        Ui.Platform = _platform;
        MakeJunk();
        var vm = Model();
        Sta.Wait(vm.StartAsync());
        _running = true;                         // запустили между проверками таймера

        Sta.Wait(vm.RemoveCommand.ExecuteAsync(null));

        Assert.Equal([Loc.T("clean.serverRunning")], _platform.Warned);
        Assert.Empty(_platform.Asked);
        Assert.Empty(_trashed);
        Assert.True(vm.IsRunning);
    });

    [Fact]
    public void Remove_PartialFailure_ReportsWhatStayed() => Sta.Run(() =>
    {
        Ui.Platform = _platform;
        MakeJunk();
        var vm = Model();
        Sta.Wait(vm.StartAsync());
        vm.Groups[2].IsChecked = true;
        _trashFails.Add(Path.Combine(_root, "ModData", "expandedfoods"));
        _platform.Answers.Enqueue(Answer.Yes);

        Sta.Wait(vm.RemoveCommand.ExecuteAsync(null));

        Assert.Equal([Path.Combine(_root, "Logs", "Archive")], _trashed);
        Assert.True(vm.HasRecycled);
        Assert.Equal(Loc.T("clean.done", WorldCopiesViewModel.SizeText(5000)) + " " + Loc.T("clean.problems", "expandedfoods: файл занят"), vm.Status);
        Assert.Equal(["Дом", "expandedfoods"], vm.Groups.Select(g => g.Items[0].Title));
    });

    /// <summary>Платформа теста: ручной таймер, ответы на вопросы по очереди, предупреждения — в список.</summary>
    private sealed class FakePlatform : IUiPlatform
    {
        public ManualTimer? Timer { get; private set; }
        public Queue<Answer> Answers { get; } = new();
        public List<string> Asked { get; } = [];
        public List<string> Warned { get; } = [];

        public IUiTimer CreateTimer(TimeSpan interval) => Timer = new ManualTimer { Interval = interval };

        public Answer Ask(string text, AskButtons buttons, Answer? byDefault = null, bool warning = false)
        {
            Asked.Add(text);
            return Answers.Count > 0 ? Answers.Dequeue() : throw new InvalidOperationException("Неожиданный вопрос: " + text);
        }

        public void Notify(string text, NoticeKind kind) => Warned.Add(text);
        public bool IsUiThread => true;
        public void Post(Action action) => action();
        public void PostIdle(Action action) => action();
        public void Invoke(Action action) => action();
        public IReadOnlyList<string>? PickFiles(string title, IReadOnlyList<FileFilter> filters, bool multiple) => throw new NotSupportedException();
        public string? PickSaveFile(string title, IReadOnlyList<FileFilter> filters, string suggestedName, string? folder = null, bool confirmOverwrite = true) =>
            throw new NotSupportedException();
        public string? PickFolder(string title, string? initialFolder) => throw new NotSupportedException();
        public void SetClipboard(string text) => throw new NotSupportedException();
        public void OpenUrl(string url) => throw new NotSupportedException();
        public void OpenFolder(string dir) => throw new NotSupportedException();
        public void ShowInFolder(string path) => throw new NotSupportedException();
        public void MoveToTrash(string path) => throw new NotSupportedException(); // уборка идёт через CleanupServices.Trash
        public void OpenTrash() => throw new NotSupportedException();
        public bool IsMainWindowVisible => false;
        public void RestartAfterClose(string exe) => throw new NotSupportedException();
        public string WriteCrashLog(Exception ex) => throw new NotSupportedException();
    }

    private sealed class ManualTimer : IUiTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsEnabled { get; private set; }
        public event EventHandler? Tick;
        public void Start() => IsEnabled = true;
        public void Stop() => IsEnabled = false;

        /// <summary>Прошёл интервал: таймер сработал (если запущен).</summary>
        public void Fire()
        {
            if (IsEnabled) Tick?.Invoke(this, EventArgs.Empty);
        }
    }
}

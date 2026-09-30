using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string _data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-tests-" + Guid.NewGuid().ToString("N"))).FullName;
    private static readonly DateTime T0 = new(2026, 9, 30, 12, 0, 0);

    public void Dispose() => Directory.Delete(_data, recursive: true);

    private string Backup(string name, string text = "x")
    {
        var path = Path.Combine(_data, "Backups", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    // ---------- хранилище ----------

    [Fact]
    public void List_ReadsTimeFromName_NewestFirst()
    {
        Backup("default-2026-09-07_07-42-31.vcdbs");
        Backup("default-2026-09-30_17-51-32.vcdbs", "12345");
        Backup("manual-copy.vcdbs");
        Backup("notes.txt");

        var list = new BackupStore(_data).List();

        Assert.Equal(3, list.Count); // только .vcdbs
        var newest = list.First(b => b.IsStamped);
        Assert.Equal("default-2026-09-30_17-51-32.vcdbs", newest.Name);
        Assert.Equal(new DateTime(2026, 9, 30, 17, 51, 32), newest.Time);
        Assert.Equal(5, newest.Size);
        Assert.False(list.Single(b => b.Name == "manual-copy.vcdbs").IsStamped);
        Assert.Empty(new BackupStore(Path.Combine(_data, "nope")).List());
    }

    [Fact]
    public void Prune_KeepsNewestStamped_AndNeverTouchesHandMadeFiles()
    {
        for (var day = 1; day <= 5; day++) Backup($"default-2026-09-0{day}_10-00-00.vcdbs");
        Backup("before-update.vcdbs");
        var store = new BackupStore(_data);

        var removed = store.Prune(2);

        Assert.Equal(["default-2026-09-03_10-00-00.vcdbs", "default-2026-09-02_10-00-00.vcdbs", "default-2026-09-01_10-00-00.vcdbs"], removed.Select(b => b.Name));
        Assert.Equal(["before-update.vcdbs", "default-2026-09-04_10-00-00.vcdbs", "default-2026-09-05_10-00-00.vcdbs"],
            store.List().Select(b => b.Name).Order());
        Assert.Empty(store.Prune(0)); // 0 — не удалять
        Assert.Equal(3, store.List().Count);
    }

    [Fact]
    public void CopySave_MakesStampedCopy_AndRefusesDirtySave()
    {
        var save = Path.Combine(_data, "Saves", "default.vcdbs");
        Directory.CreateDirectory(Path.GetDirectoryName(save)!);
        File.WriteAllText(save, "world");
        var store = new BackupStore(_data);

        var copy = store.CopySave(save, T0);
        Assert.Equal("world-2026-09-30_12-00-00.vcdbs", copy.Name);
        Assert.Equal("world", File.ReadAllText(copy.Path));
        Assert.True(store.List().Single().IsStamped);

        // рядом непустой журнал SQLite — сервер упал, файл мира может быть неполным
        File.WriteAllText(save + "-wal", "pending");
        Assert.Throws<InvalidOperationException>(() => store.CopySave(save, T0.AddMinutes(1)));
        Assert.Throws<FileNotFoundException>(() => store.CopySave(Path.Combine(_data, "Saves", "none.vcdbs"), T0));
    }

    [Fact]
    public void ProfileName_GoesIntoFileName_AndRotationTouchesOnlyOwnCopies()
    {
        Assert.Equal("Дуо_с_женой", BackupStore.Slug("  Дуо с женой "));
        Assert.Equal("a_b_c", BackupStore.Slug("a:b?c."));
        Assert.Equal("world", BackupStore.Slug("  "));

        var store = new BackupStore(_data, "Дуо_с_женой");
        Assert.Equal("Дуо_с_женой-2026-09-30_12-00-00.vcdbs", store.NameFor(T0));

        for (var day = 1; day <= 4; day++) Backup($"Дуо_с_женой-2026-09-0{day}_10-00-00.vcdbs");
        Backup("default-2026-08-01_10-00-00.vcdbs");            // прежняя копия сервера
        Backup("Соло-2026-08-02_10-00-00.vcdbs");               // принесли из другого профиля
        Backup("Дуо_с_женой-before-2026-08-03_10-00-00.vcdbs"); // своя приставка, но имя не по шаблону

        var list = store.List();
        Assert.Equal(7, list.Count(b => b.IsStamped));
        Assert.Equal(4, list.Count(b => b.IsOwn));

        var removed = store.Prune(1);
        Assert.Equal(3, removed.Count);
        Assert.All(removed, b => Assert.StartsWith("Дуо_с_женой-2026-09-0", b.Name));
        Assert.Equal(["default-2026-08-01_10-00-00.vcdbs", "Дуо_с_женой-2026-09-04_10-00-00.vcdbs", "Дуо_с_женой-before-2026-08-03_10-00-00.vcdbs", "Соло-2026-08-02_10-00-00.vcdbs"],
            store.List().Select(b => b.Name).Order(StringComparer.Ordinal));
        Assert.NotNull(store.Find("соло-2026-08-02_10-00-00.vcdbs"));
        Assert.Null(store.Find("nope.vcdbs"));
    }

    // ---------- расписание ----------

    private static readonly ServerAutomation Hourly = new() { BackupEnabled = true, BackupIntervalHours = 1, BackupOnlyWhenPlayed = false };

    [Fact]
    public void Schedule_CountsFromServerStart_ThenFromLastBackup()
    {
        var s = new BackupScheduler();
        Assert.Null(s.NextAt(Hourly, ServerState.Stopped, null));
        Assert.Null(s.NextAt(Hourly with { BackupEnabled = false }, ServerState.Running, T0));
        Assert.Equal(T0.AddHours(1), s.NextAt(Hourly, ServerState.Running, T0));

        Assert.False(s.IsDue(Hourly, T0.AddMinutes(59), ServerState.Running, T0));
        Assert.False(s.IsDue(Hourly, T0.AddHours(2), ServerState.Starting, T0)); // ещё не поднялся
        Assert.True(s.IsDue(Hourly, T0.AddHours(1), ServerState.Running, T0));

        s.MarkDone(T0.AddHours(1), playersOnline: 0);
        Assert.Equal(T0.AddHours(1), s.LastBackupAt);
        Assert.False(s.IsDue(Hourly, T0.AddHours(1).AddMinutes(30), ServerState.Running, T0));
        Assert.True(s.IsDue(Hourly, T0.AddHours(2), ServerState.Running, T0));
    }

    [Fact]
    public void Schedule_AfterDowntime_WaitsFullIntervalFromStart()
    {
        var s = new BackupScheduler();
        s.Seed(T0.AddDays(-2)); // на диске старая копия, сервер только что запустили
        Assert.Equal(T0.AddHours(1), s.NextAt(Hourly, ServerState.Running, T0));

        s.Seed(T0.AddMinutes(10)); // копию сделали уже после запуска
        Assert.Equal(T0.AddMinutes(70), s.NextAt(Hourly, ServerState.Running, T0));
    }

    [Fact]
    public void Schedule_OnlyWhenPlayed_SkipsEmptyIntervals()
    {
        var played = Hourly with { BackupOnlyWhenPlayed = true };
        var s = new BackupScheduler();

        // час прошёл, никого не было — пропуск, отсчёт заново
        Assert.False(s.IsDue(played, T0.AddHours(1), ServerState.Running, T0));
        Assert.Equal(T0.AddHours(2), s.NextAt(played, ServerState.Running, T0));

        // кто-то зашёл — копия в конце текущего интервала, не сразу
        s.NotePlayers(1);
        Assert.False(s.IsDue(played, T0.AddHours(1).AddMinutes(5), ServerState.Running, T0));
        Assert.True(s.IsDue(played, T0.AddHours(2), ServerState.Running, T0));

        // игрок остался — следующая копия тоже нужна; ушёл — нет
        s.MarkDone(T0.AddHours(2), playersOnline: 1);
        Assert.True(s.IsDue(played, T0.AddHours(3), ServerState.Running, T0));
        s.MarkDone(T0.AddHours(3), playersOnline: 0);
        Assert.False(s.IsDue(played, T0.AddHours(4), ServerState.Running, T0));
    }

    [Fact]
    public void Automation_RoundTripsThroughFile_AndClampsInterval()
    {
        var dir = Path.Combine(_data, "agents");
        Assert.False(ServerAutomation.Load("p1", dir).BackupEnabled); // файла нет — всё выключено

        new ServerAutomation { BackupEnabled = true, BackupIntervalHours = 2.5, BackupKeep = 10, BackupOnlyWhenPlayed = false }.Save("p1", dir);
        var loaded = ServerAutomation.Load("p1", dir);
        Assert.True(loaded.BackupEnabled);
        Assert.Equal(2.5, loaded.BackupIntervalHours);
        Assert.Equal(10, loaded.BackupKeep);
        Assert.False(loaded.BackupOnlyWhenPlayed);

        Assert.Equal(TimeSpan.FromMinutes(5), new ServerAutomation { BackupIntervalHours = 0 }.BackupInterval);
        File.WriteAllText(ServerAutomation.FileFor("bad", dir), "{ not json");
        Assert.False(ServerAutomation.Load("bad", dir).BackupEnabled);
    }
}

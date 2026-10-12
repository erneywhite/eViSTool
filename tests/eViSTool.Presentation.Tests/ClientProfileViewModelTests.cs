using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;
using Newtonsoft.Json.Linq;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Окно нового клиентского профиля (и клона): папка по названию, пока её не правили руками; пересчёт плана после паузы
/// и отказ от устаревшего ответа; запущенная игра, свободное место, ошибки плана; размер миров; копирование с ходом,
/// отменой и сбоями — ветка за веткой. Система подменена (план, копирование, процессы, диск), таймер
/// паузы ручной; в конце — один проход с настоящим планом и копированием во временной папке.
/// </summary>
public sealed class ClientProfileViewModelTests : IDisposable
{
    private const long PlanBytes = 1500;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-cpvm-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;        // папка данных основы (есть, конфига нет — моды из её Mods)
    private readonly string _defaultData; // «стандартная папка данных» для основы без своей папки
    private readonly GameProfile _source;
    private readonly IUiPlatform? _oldPlatform = Ui.IsSet ? Ui.Platform : null;
    private readonly FakePlatform _platform = new();

    // подменённая система
    private readonly List<ClientCloneOptions> _planned = [];
    private Func<ClientCloneOptions, ClientClonePlan>? _plan;
    private bool _running;
    private int _runningAsked;
    private long? _free = 1L << 40;
    private long _worlds;
    private string? _worldsAskedFor;
    private Func<ClientClonePlan, IProgress<CloneProgress>, CancellationToken, Task<GameProfile>> _apply =
        (_, _, _) => throw new Xunit.Sdk.XunitException("копирование в этом тесте не ожидалось");

    public ClientProfileViewModelTests()
    {
        _data = Directory.CreateDirectory(Path.Combine(_root, "Основа")).FullName;
        _defaultData = Path.Combine(_root, "VintagestoryData");
        _source = new GameProfile { Name = "Основной", Kind = ProfileKind.Client, DataDir = _data };
    }

    public void Dispose()
    {
        Ui.Platform = _oldPlatform!; // как было (null — не задано: следующий тест поставит своё)
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ClientProfileServices Services => new()
    {
        Plan = (source, options) =>
        {
            lock (_planned) _planned.Add(options);
            return _plan is { } plan ? plan(options) : Plan(options);
        },
        IsGameRunning = _ =>
        {
            Interlocked.Increment(ref _runningAsked);
            return _running;
        },
        FreeSpace = _ => _free,
        WorldBytes = dir =>
        {
            _worldsAskedFor = dir;
            return _worlds;
        },
        Apply = (plan, progress, ct) => _apply(plan, progress, ct),
        DefaultDataDir = _defaultData,
    };

    /// <summary>Модель на подменённой системе. Платформа ставится здесь: модель берёт таймер паузы при создании.</summary>
    private ClientProfileViewModel Model(bool clone = false, string name = "Клиент 2", GameProfile? source = null)
    {
        Ui.Platform = _platform;
        return new ClientProfileViewModel(source ?? _source, clone, name, Services);
    }

    /// <summary>Модель, у которой первый расчёт плана (при открытии) уже пришёл.</summary>
    private ClientProfileViewModel Ready(bool clone = false)
    {
        var vm = Model(clone);
        Sta.Wait(vm.Calculation);
        return vm;
    }

    private ClientClonePlan Plan(ClientCloneOptions options, long bytes = PlanBytes, IReadOnlyList<string>? sources = null, IReadOnlyList<string>? notes = null) => new()
    {
        Source = _source,
        Options = options,
        Files = [new CloneFile(Path.Combine(_data, "a.json"), "a.json", bytes - 500), new CloneFile(Path.Combine(_data, "b.json"), "b.json", 500)],
        SettingsName = ClientProfileCloner.DefaultSettingsName,
        ModDirs = [],
        ModSources = sources ?? [],
        ModNotes = notes ?? [],
    };

    private static string SummaryOf(long bytes, long? free, string mods = "") =>
        Loc.T("clone.summary", 2, Sizes.Format(bytes), free is { } f ? Sizes.Format(f) : "—") + mods + " " + Loc.T("cprofile.summarySkipped");

    private ClientCloneOptions LastPlanned
    {
        get { lock (_planned) return _planned[^1]; }
    }

    private int PlannedCount
    {
        get { lock (_planned) return _planned.Count; }
    }

    /// <summary>Пауза в правке прошла: план считается заново, дождаться ответа.</summary>
    private void Settle(ClientProfileViewModel vm)
    {
        Assert.True(_platform.Timer.IsEnabled, "после правки должен быть запущен таймер паузы");
        _platform.Timer.Fire();
        Sta.Wait(vm.Calculation);
    }

    // ---- открытие

    [Fact]
    public void New_Opens_WithTextsSuggestedNameAndFolder_AndCalculatesAtOnce() => Sta.Run(() =>
    {
        var vm = Model();

        Assert.Equal(Loc.T("newprofile.titleClient"), vm.Title);
        Assert.Equal(Loc.T("newprofile.titleClient"), vm.Heading);
        Assert.Equal(Loc.T("cprofile.introNew", "Основной"), vm.Intro);
        Assert.Equal(Loc.T("newprofile.create"), vm.GoText);
        Assert.Equal(Loc.T("cprofile.modsSharedHint", "Основной", Path.Combine(_data, "Mods")), vm.SharedModsHint);
        Assert.True(vm.HasMods);
        Assert.Equal(ClientModsMode.Shared, vm.Mods);
        Assert.True(vm.CopySettings);
        Assert.False(vm.CopyWorlds);
        Assert.False(vm.HasWorlds);
        Assert.Equal("Клиент 2", vm.Name);
        Assert.Equal(Path.Combine(_data, ClientProfileLayout.ContainerName, "Клиент 2"), vm.TargetDir);
        // план считается сразу, без паузы; пока ответа нет — кнопка выключена
        Assert.Equal(ClientProfileViewModel.RecalculateDelay, _platform.Timer.Interval);
        Assert.Equal(TimeSpan.FromMilliseconds(300), _platform.Timer.Interval);
        Assert.False(_platform.Timer.IsEnabled);
        Assert.False(vm.CanGo);
        Assert.False(vm.GoCommand.CanExecute(null));

        Sta.Wait(vm.Calculation);

        Assert.Equal(1, PlannedCount);
        Assert.Equal(new ClientCloneOptions { Name = "Клиент 2", TargetDir = vm.TargetDir, Mods = ClientModsMode.Shared, CopySettings = true, CopyWorlds = false },
            LastPlanned);
        Assert.Equal(SummaryOf(PlanBytes, _free), vm.Summary);
        Assert.Equal("", vm.ErrorText);
        Assert.False(vm.IsGameRunning);
        Assert.Equal(0, _runningAsked); // миры не копируются — запущенная игра не мешает, её и не спрашиваем
        Assert.True(vm.CanGo);
        Assert.True(vm.GoCommand.CanExecute(null));
        Assert.False(vm.IsCopying);
    });

    [Fact]
    public void Clone_Opens_WithCloneTexts() => Sta.Run(() =>
    {
        var vm = Model(clone: true, name: "Основной (копия)");

        Assert.Equal(Loc.T("clone.title"), vm.Title);
        Assert.Equal(Loc.T("clone.heading", "Основной"), vm.Heading);
        Assert.Equal(Loc.T("cprofile.introClone"), vm.Intro);
        Assert.Equal(Loc.T("clone.go"), vm.GoText);
        Assert.Equal(Path.Combine(_data, ClientProfileLayout.ContainerName, "Основной (копия)"), vm.TargetDir);
    });

    [Fact]
    public void SharedModsHint_ListsAllModFoldersOfTheSource()
    {
        var other = Directory.CreateDirectory(Path.Combine(_root, "ЕщёМоды")).FullName;
        var own = Path.Combine(_data, "Mods");
        File.WriteAllText(Path.Combine(_data, "clientsettings.json"),
            new JObject { ["stringListSettings"] = new JObject { ["modPaths"] = new JArray("Mods", own, other) } }.ToString());

        Sta.Run(() =>
        {
            var vm = Model();
            Assert.Equal(Loc.T("cprofile.modsSharedHint", "Основной", own + "; " + other), vm.SharedModsHint);
            Assert.True(vm.HasMods);
        });
    }

    [Fact]
    public void SourceWithoutDataFolder_HasNoMods_OnlyEmpty_FolderFromStandardData() => Sta.Run(() =>
    {
        // игра с основой ещё не запускалась: папки данных нет — делить и копировать нечего
        var source = new GameProfile { Name = "Новый", Kind = ProfileKind.Client };

        var vm = Model(source: source);

        Assert.False(vm.HasMods);
        Assert.Equal(ClientModsMode.Empty, vm.Mods);
        Assert.Equal(Loc.T("cprofile.modsSharedHint", "Новый", "—"), vm.SharedModsHint);
        // у основы нет своей папки — новая предлагается в стандартной
        Assert.Equal(Path.Combine(_defaultData, ClientProfileLayout.ContainerName, "Клиент 2"), vm.TargetDir);
        Sta.Wait(vm.Calculation);
        Assert.Equal(ClientModsMode.Empty, LastPlanned.Mods);
    });

    [Fact]
    public void SourceWithMissingDataFolder_HasNoMods_FolderNextToIt() => Sta.Run(() =>
    {
        var missing = Path.Combine(_root, "нет такой");
        var vm = Model(source: new GameProfile { Name = "Старый", Kind = ProfileKind.Client, DataDir = missing });

        Assert.False(vm.HasMods);
        Assert.Equal(ClientModsMode.Empty, vm.Mods);
        Assert.Equal(Path.Combine(missing, ClientProfileLayout.ContainerName, "Клиент 2"), vm.TargetDir);
    });

    [Fact]
    public void EmptySuggestedName_NoFolder() => Sta.Run(() =>
    {
        var vm = Model(name: "  ");
        Assert.Equal("", vm.TargetDir);
    });

    // ---- название и папка

    [Fact]
    public void Folder_FollowsName_UntilEditedByHand() => Sta.Run(() =>
    {
        var vm = Ready();

        vm.Name = "  Мой мир  ";
        Assert.Equal(Path.Combine(_data, ClientProfileLayout.ContainerName, "Мой мир"), vm.TargetDir);
        vm.Name = " ";
        Assert.Equal("", vm.TargetDir); // название стёрли — и папку
        vm.Name = "Третий";
        Assert.Equal(Path.Combine(_data, ClientProfileLayout.ContainerName, "Третий"), vm.TargetDir);

        vm.TargetDir = @"D:\Игры\Третий";
        vm.Name = "Четвёртый";
        Assert.Equal(@"D:\Игры\Третий", vm.TargetDir); // папку поправили руками — по названию больше не подставляем

        vm.TargetDir = "";
        vm.Name = "Пятый";
        Assert.Equal("", vm.TargetDir); // даже если потом её стёрли
    });

    [Fact]
    public void SuggestedFolder_SkipsBusyFolder()
    {
        var busy = Directory.CreateDirectory(Path.Combine(_data, ClientProfileLayout.ContainerName, "Клиент 2")).FullName;
        File.WriteAllText(Path.Combine(busy, "x.txt"), "занято");

        Sta.Run(() => Assert.Equal(busy + "-2", Model().TargetDir));
    }

    [Fact]
    public void Options_GoToPlanTrimmed_AfterPause() => Sta.Run(() =>
    {
        var vm = Ready();

        vm.Name = "  Мой  ";
        vm.TargetDir = @"  C:\Профили\Мой  ";
        vm.Mods = ClientModsMode.Copy;
        vm.CopySettings = false;
        vm.CopyWorlds = true;

        // каждая правка сбрасывает план и откладывает расчёт; считается один раз, после паузы
        Assert.False(vm.CanGo);
        Assert.Equal(1, PlannedCount);
        Settle(vm);

        Assert.Equal(2, PlannedCount);
        Assert.Equal(new ClientCloneOptions { Name = "Мой", TargetDir = @"C:\Профили\Мой", Mods = ClientModsMode.Copy, CopySettings = false, CopyWorlds = true },
            LastPlanned);
        Assert.False(_platform.Timer.IsEnabled);
        Assert.True(vm.CanGo);
    });

    [Theory]
    [InlineData(ClientModsMode.Copy)]
    [InlineData(ClientModsMode.Empty)]
    public void EveryOption_Recalculates(ClientModsMode mods) => Sta.Run(() =>
    {
        var vm = Ready();
        vm.Mods = mods;
        Settle(vm);
        Assert.Equal(mods, LastPlanned.Mods);

        vm.CopySettings = false;
        Settle(vm);
        Assert.False(LastPlanned.CopySettings);

        vm.CopyWorlds = true;
        Settle(vm);
        Assert.True(LastPlanned.CopyWorlds);
        vm.CopyWorlds = false;
        Settle(vm);
        Assert.False(LastPlanned.CopyWorlds);
    });

    // ---- устаревший ответ

    [Fact]
    public void StaleCalculation_IsDropped_NewOneShown() => Sta.Run(() =>
    {
        using var slow = new ManualResetEventSlim(false);
        _plan = o =>
        {
            // задерживается расчёт при открытии (старое имя) — по имени, а не по порядку вызова: под нагрузкой фоновый
            // расчёт открытия мог стартовать позже расчёта для нового имени, и тогда ждал бы не тот
            if (o.Name != "Новое") slow.Wait(TimeSpan.FromSeconds(20));
            return Plan(o, bytes: o.Name == "Новое" ? 4000 : PlanBytes);
        };
        var vm = Model();
        var stale = vm.Calculation;

        vm.Name = "Новое";
        Settle(vm);
        Assert.Equal(SummaryOf(4000, _free), vm.Summary);
        Assert.True(vm.CanGo);

        slow.Set();
        Sta.Wait(stale);
        Assert.Equal(SummaryOf(4000, _free), vm.Summary); // ответ старого расчёта не записан поверх
        Assert.True(vm.CanGo);
    });

    [Fact]
    public void EditDuringCalculation_DropsItsAnswer_EvenBeforeNewOne() => Sta.Run(() =>
    {
        using var slow = new ManualResetEventSlim(false);
        _plan = o =>
        {
            slow.Wait(TimeSpan.FromSeconds(20));
            return Plan(o);
        };
        var vm = Model();
        var first = vm.Calculation;

        vm.CopySettings = false; // правка, пауза ещё не прошла
        slow.Set();
        Sta.Wait(first);

        Assert.Equal("", vm.Summary);
        Assert.False(vm.CanGo);
        Settle(vm);
        Assert.True(vm.CanGo);
    });

    // ---- ошибки плана

    public static TheoryData<int> PlanErrors => new() { 0, 1, 2, 3, 4, 5 };

    private static Exception PlanError(int i) => i switch
    {
        0 => new InvalidOperationException("Укажи название"),
        1 => new IOException("диск занят"),
        2 => new UnauthorizedAccessException("нет доступа"),
        3 => new ArgumentException("плохой путь"),
        4 => new NotSupportedException("не поддерживается"),
        _ => new Newtonsoft.Json.JsonReaderException("битый clientsettings.json"),
    };

    [Theory]
    [MemberData(nameof(PlanErrors))]
    public void PlanError_ShowsMessage_AndBlocksGo(int error) => Sta.Run(() =>
    {
        var ex = PlanError(error);
        _plan = _ => throw ex;

        var vm = Ready();

        Assert.Equal(Loc.T("cprofile.summarySkipped"), vm.Summary);
        Assert.Equal(ex.Message, vm.ErrorText);
        Assert.False(vm.CanGo);
        Assert.False(vm.GoCommand.CanExecute(null));
    });

    [Fact]
    public void PlanError_Fixed_ClearsMessage() => Sta.Run(() =>
    {
        _plan = o => o.Name.Length == 0 ? throw new InvalidOperationException(Loc.T("clone.errName")) : Plan(o);
        var vm = Ready();

        vm.Name = "";
        Settle(vm);
        Assert.Equal(Loc.T("clone.errName"), vm.ErrorText);
        Assert.False(vm.CanGo);

        vm.Name = "Снова";
        Settle(vm);
        Assert.Equal("", vm.ErrorText);
        Assert.Equal(SummaryOf(PlanBytes, _free), vm.Summary);
        Assert.True(vm.CanGo);
    });

    // ---- запущенная игра

    [Fact]
    public void RunningGame_WithoutWorlds_DoesNotMatter() => Sta.Run(() =>
    {
        _running = true;
        var vm = Ready();

        Assert.Equal(0, _runningAsked);
        Assert.False(vm.IsGameRunning);
        Assert.True(vm.CanGo);
    });

    [Fact]
    public void RunningGame_WithWorlds_WarnsAndBlocksGo() => Sta.Run(() =>
    {
        _running = true;
        var vm = Ready();

        vm.CopyWorlds = true;
        Settle(vm);

        Assert.Equal(1, _runningAsked);
        Assert.True(vm.IsGameRunning);
        Assert.Equal(SummaryOf(PlanBytes, _free), vm.Summary); // план показан, но начать нельзя
        Assert.Equal("", vm.ErrorText);
        Assert.False(vm.CanGo);

        // без миров — снова можно
        vm.CopyWorlds = false;
        Settle(vm);
        Assert.False(vm.IsGameRunning);
        Assert.True(vm.CanGo);

        // игру закрыли
        _running = false;
        vm.CopyWorlds = true;
        Settle(vm);
        Assert.False(vm.IsGameRunning);
        Assert.True(vm.CanGo);
    });

    [Fact]
    public void RunningGame_PlanError_ShowsOnlyTheError() => Sta.Run(() =>
    {
        // план не составился — про запущенную игру уже не спрашиваем
        _running = true;
        _plan = _ => throw new InvalidOperationException("папка не пустая");
        var vm = Ready();

        vm.CopyWorlds = true;
        Settle(vm);

        Assert.Equal(0, _runningAsked);
        Assert.False(vm.IsGameRunning);
        Assert.Equal("папка не пустая", vm.ErrorText);
    });

    // ---- место на диске и сводка

    [Fact]
    public void FreeSpaceUnknown_ShowsDash_AndAllowsGo() => Sta.Run(() =>
    {
        _free = null;
        var vm = Ready();
        Assert.Equal(SummaryOf(PlanBytes, null), vm.Summary);
        Assert.Equal("", vm.ErrorText);
        Assert.True(vm.CanGo);
    });

    [Fact]
    public void NotEnoughSpace_IsAnError() => Sta.Run(() =>
    {
        _free = PlanBytes - 1;
        var vm = Ready();
        Assert.Equal(SummaryOf(PlanBytes, PlanBytes - 1), vm.Summary);
        Assert.Equal(Loc.T("clone.noSpace"), vm.ErrorText);
        Assert.False(vm.CanGo);
    });

    [Fact]
    public void JustEnoughSpace_IsFine() => Sta.Run(() =>
    {
        _free = PlanBytes;
        var vm = Ready();
        Assert.Equal("", vm.ErrorText);
        Assert.True(vm.CanGo);
    });

    [Fact]
    public void FreeSpace_IsAskedForTrimmedTarget() => Sta.Run(() =>
    {
        string? asked = null;
        Ui.Platform = _platform;
        var vm = new ClientProfileViewModel(_source, false, "Клиент 2", Services with
        {
            FreeSpace = dir =>
            {
                asked = dir;
                return null;
            },
        });
        Sta.Wait(vm.Calculation);
        Assert.Equal(vm.TargetDir, asked);

        vm.TargetDir = @"  E:\Профили  ";
        Settle(vm);
        Assert.Equal(@"E:\Профили", asked);
    });

    [Fact]
    public void CopiedMods_SourcesAndNotesInSummary() => Sta.Run(() =>
    {
        IReadOnlyList<string> sources = [@"C:\Моды", @"D:\Ещё"];
        IReadOnlyList<string> notes = ["carryon_1.8.0.zip — дубль, взят новее"];
        _plan = o => Plan(o, sources: o.Mods == ClientModsMode.Copy ? sources : [], notes: o.Mods == ClientModsMode.Copy ? notes : []);
        var vm = Ready();

        vm.Mods = ClientModsMode.Copy;
        Settle(vm);

        Assert.Equal(SummaryOf(PlanBytes, _free, ProfileCopyText.ModsText(sources, notes)), vm.Summary);
    });

    // ---- миры

    [Fact]
    public void NoWorlds_CheckBoxHidden() => Sta.Run(() =>
    {
        var vm = Ready(clone: true);
        Sta.Wait(vm.LoadAsync());

        Assert.Equal(_data, _worldsAskedFor);
        Assert.False(vm.HasWorlds);
        Assert.Equal("", vm.WorldsText);
        Assert.False(vm.CopyWorlds); // даже у клона
        Assert.False(_platform.Timer.IsEnabled);
    });

    [Fact]
    public void Worlds_New_ShownUnchecked() => Sta.Run(() =>
    {
        _worlds = 5L << 20;
        var vm = Ready();
        Sta.Wait(vm.LoadAsync());

        Assert.True(vm.HasWorlds);
        Assert.Equal(Loc.T("cprofile.worlds", Sizes.Format(5L << 20)), vm.WorldsText);
        Assert.False(vm.CopyWorlds);
        Assert.False(_platform.Timer.IsEnabled); // ничего не поменялось — план прежний
        Assert.True(vm.CanGo);
    });

    [Fact]
    public void Worlds_Clone_CheckedAndRecalculated() => Sta.Run(() =>
    {
        _worlds = 3L << 30;
        var vm = Ready(clone: true);
        Sta.Wait(vm.LoadAsync());

        Assert.True(vm.HasWorlds);
        Assert.Equal(Loc.T("cprofile.worlds", Sizes.Format(3L << 30)), vm.WorldsText);
        Assert.True(vm.CopyWorlds);
        Assert.False(vm.CanGo);
        Settle(vm);
        Assert.True(LastPlanned.CopyWorlds);
        Assert.True(vm.CanGo);
    });

    [Fact]
    public void WorldBytes_CountsOnlyWorldFolders()
    {
        void Write(string relative, int size)
        {
            var path = Path.Combine(_data, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[size]);
        }
        Write(Path.Combine("Saves", "Дом.vcdbs"), 100);
        Write(Path.Combine("Maps", "sub", "Дом.db"), 50);
        Write(Path.Combine("Playerdata", "p.json"), 25);
        Write(Path.Combine("WorldEdit", "w.json"), 5);
        Write(Path.Combine("Mods", "carryon.zip"), 1000);   // не мир
        Write(Path.Combine("Backups", "old.vcdbs"), 1000);  // не мир
        Write("clientsettings.json", 1000);                 // не мир

        Assert.Equal(180, ClientProfileViewModel.WorldBytes(_data));
        Assert.Equal(0, ClientProfileViewModel.WorldBytes(null));
        Assert.Equal(0, ClientProfileViewModel.WorldBytes("  "));
        Assert.Equal(0, ClientProfileViewModel.WorldBytes(Path.Combine(_root, "нет такой")));
    }

    // ---- «Обзор…»

    [Fact]
    public void Browse_StartsAtNearestExistingFolder_PickedFolderCountsAsEdited() => Sta.Run(() =>
    {
        var picked = Directory.CreateDirectory(Path.Combine(_root, "Выбранная")).FullName;
        _platform.Folder = picked;
        var vm = Ready();

        vm.BrowseCommand.Execute(null);

        // предложенной папки ещё нет — начинаем с ближайшей существующей над ней (папка основы)
        Assert.Equal(_data, _platform.FolderStart);
        Assert.Equal(Loc.T("clone.pickTarget"), _platform.FolderTitle);
        Assert.Equal(picked, vm.TargetDir);
        vm.Name = "Другое";
        Assert.Equal(picked, vm.TargetDir);
        Settle(vm);
        Assert.Equal(picked, LastPlanned.TargetDir);
    });

    [Fact]
    public void Browse_Cancelled_KeepsFolderFollowingName() => Sta.Run(() =>
    {
        _platform.Folder = null;
        var vm = Ready();
        var before = vm.TargetDir;

        vm.BrowseCommand.Execute(null);

        Assert.Equal(before, vm.TargetDir);
        Assert.False(_platform.Timer.IsEnabled);
        vm.Name = "Другое";
        Assert.Equal(Path.Combine(_data, ClientProfileLayout.ContainerName, "Другое"), vm.TargetDir);
    });

    [Fact]
    public void Browse_EmptyFolder_StartsNowhere() => Sta.Run(() =>
    {
        var vm = Model(name: "");
        vm.BrowseCommand.Execute(null);
        Assert.Equal("", _platform.FolderStart);
    });

    // ---- копирование

    [Fact]
    public void Go_CopiesWithProgress_AndFinishesWithProfile() => Sta.Run(() =>
    {
        var gate = new TaskCompletionSource();
        var created = new GameProfile { Name = "Клиент 2", Kind = ProfileKind.Client, DataDir = Path.Combine(_root, "new") };
        ClientClonePlan? applied = null;
        _apply = async (plan, progress, _) =>
        {
            applied = plan;
            progress.Report(new CloneProgress(375, 9999, @"Saves\Дом.vcdbs"));
            await gate.Task;
            return created;
        };
        var vm = Ready();
        GameProfile? finished = null;
        vm.Finished += p => finished = p;

        var go = vm.GoCommand.ExecuteAsync(null);

        Assert.True(vm.IsCopying);
        Assert.False(vm.CanGo);
        Assert.False(vm.GoCommand.CanExecute(null));
        Sta.WaitFor(() => vm.ProgressText.Length > 0);
        Assert.Equal(375.0 / PlanBytes, vm.ProgressValue); // доля — от объёма плана
        Assert.Equal(Loc.T("clone.copying", @"Saves\Дом.vcdbs", Sizes.Format(375), Sizes.Format(9999)), vm.ProgressText);
        Assert.Null(finished);

        gate.SetResult();
        Sta.Wait(go);

        Assert.Same(created, finished);
        Assert.Same(created, vm.Result);
        Assert.Equal("Клиент 2", applied!.Options.Name);
        Assert.False(vm.CancelCopy()); // копирование кончилось — окно закрывается
    });

    [Fact]
    public void Go_EmptyPlan_ProgressShareDoesNotDivideByZero() => Sta.Run(() =>
    {
        _plan = o => Plan(o) with { Files = [] };
        var gate = new TaskCompletionSource();
        _apply = async (_, progress, _) =>
        {
            progress.Report(new CloneProgress(0, 0, "clientsettings.json"));
            await gate.Task;
            return new GameProfile();
        };
        var vm = Ready();

        var go = vm.GoCommand.ExecuteAsync(null);
        Sta.WaitFor(() => vm.ProgressText.Length > 0);
        Assert.Equal(0, vm.ProgressValue);
        gate.SetResult();
        Sta.Wait(go);
    });

    [Fact]
    public void Cancel_DuringCopy_StopsIt_WindowStays_ReasonUntilNextEdit() => Sta.Run(() =>
    {
        var started = new TaskCompletionSource();
        var token = CancellationToken.None;
        _apply = async (_, _, ct) =>
        {
            token = ct;
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new GameProfile();
        };
        var vm = Ready();
        var finished = false;
        vm.Finished += _ => finished = true;

        var go = vm.GoCommand.ExecuteAsync(null);
        Sta.WaitFor(() => started.Task.IsCompleted);

        Assert.True(vm.CancelCopy()); // «Отмена» / Escape / крестик: окно остаётся
        Sta.Wait(go);

        Assert.True(token.IsCancellationRequested);
        Assert.False(finished);
        Assert.Null(vm.Result);
        Assert.False(vm.IsCopying);
        Assert.False(vm.CancelCopy()); // теперь окно можно закрыть
        Sta.Wait(vm.Calculation);      // форма вернулась, план пересчитан
        Assert.Equal(Loc.T("clone.cancelled"), vm.ErrorText);
        Assert.True(vm.CanGo);

        // итог держится до следующей правки
        vm.CopySettings = false;
        Settle(vm);
        Assert.Equal("", vm.ErrorText);
    });

    public static TheoryData<int> CopyErrors => new() { 0, 1, 2, 3 };

    private static Exception CopyError(int i) => i switch
    {
        0 => new IOException("диск занят"),
        1 => new UnauthorizedAccessException("нет доступа"),
        2 => new Newtonsoft.Json.JsonReaderException("битый clientsettings.json"),
        _ => new InvalidOperationException("в папке назначения что-то появилось"),
    };

    [Theory]
    [MemberData(nameof(CopyErrors))]
    public void CopyFailed_FormReturnsWithReason_NoCrashLog(int error) => Sta.Run(() =>
    {
        var ex = CopyError(error);
        _apply = (_, _, _) => throw ex;
        var vm = Ready();

        Sta.Wait(vm.GoCommand.ExecuteAsync(null));

        Assert.False(vm.IsCopying);
        Assert.Empty(_platform.CrashLogs);
        Sta.Wait(vm.Calculation);
        Assert.Equal(Loc.T("clone.failed", ex.Message), vm.ErrorText);
        Assert.True(vm.CanGo); // можно попробовать ещё раз
    });

    [Fact]
    public void CopyUnexpectedError_IsLogged_FormReturns() => Sta.Run(() =>
    {
        var ex = new NullReferenceException("неожиданное");
        _apply = (_, _, _) => throw ex;
        var vm = Ready();

        Sta.Wait(vm.GoCommand.ExecuteAsync(null));

        Assert.Same(ex, Assert.Single(_platform.CrashLogs));
        Assert.False(vm.IsCopying);
        Assert.False(vm.CancelCopy());
        Sta.Wait(vm.Calculation);
        Assert.Equal(Loc.T("clone.failed", ex.Message), vm.ErrorText);
        Assert.True(vm.CanGo);
    });

    [Fact]
    public void CopyFailed_ThenNoSpace_ShowsNoSpace() => Sta.Run(() =>
    {
        var vm = Ready();
        _apply = (_, _, _) =>
        {
            _free = 10; // пока копировали, место кончилось
            throw new IOException("диск полон");
        };

        Sta.Wait(vm.GoCommand.ExecuteAsync(null));
        Sta.Wait(vm.Calculation);

        Assert.Equal(Loc.T("clone.noSpace"), vm.ErrorText); // нехватка места важнее итога
        Assert.False(vm.CanGo);
    });

    [Fact]
    public void CopyFailed_ThenPlanError_ShowsCopyReason() => Sta.Run(() =>
    {
        var vm = Ready();
        _apply = (_, _, _) =>
        {
            _plan = _ => throw new InvalidOperationException("папка не пустая"); // недокопированное осталось
            throw new IOException("диск занят");
        };

        Sta.Wait(vm.GoCommand.ExecuteAsync(null));
        Sta.Wait(vm.Calculation);

        Assert.Equal(Loc.T("cprofile.summarySkipped"), vm.Summary);
        Assert.Equal(Loc.T("clone.failed", "диск занят"), vm.ErrorText); // итог копирования важнее ошибки плана
        Assert.False(vm.CanGo);
    });

    [Fact]
    public void CancelCopy_NotCopying_LetsWindowClose() => Sta.Run(() => Assert.False(Ready().CancelCopy()));

    // ---- настоящий план и копирование

    [Fact]
    public void RealClone_CopiesSettingsModsAndWorlds_IntoSuggestedFolder()
    {
        var mods = Directory.CreateDirectory(Path.Combine(_data, "Mods")).FullName;
        File.WriteAllText(Path.Combine(mods, "carryon_1.8.0.zip"), "мод");
        File.WriteAllText(Path.Combine(_data, "clientsettings.json"),
            new JObject { ["stringListSettings"] = new JObject { ["modPaths"] = new JArray("Mods", mods) } }.ToString());
        Directory.CreateDirectory(Path.Combine(_data, "ModConfig"));
        File.WriteAllText(Path.Combine(_data, "ModConfig", "carryon.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_data, "Saves"));
        File.WriteAllBytes(Path.Combine(_data, "Saves", "Дом.vcdbs"), new byte[4096]);
        Directory.CreateDirectory(Path.Combine(_data, "Logs"));
        File.WriteAllText(Path.Combine(_data, "Logs", "client-main.log"), "журнал");

        Sta.Run(() =>
        {
            Ui.Platform = _platform;
            var vm = new ClientProfileViewModel(_source, clone: true, "Основной (копия)", new ClientProfileServices { IsGameRunning = _ => false });
            Sta.Wait(vm.Calculation);
            Sta.Wait(vm.LoadAsync());
            Assert.True(vm.CopyWorlds);
            vm.Mods = ClientModsMode.Copy;
            Settle(vm);
            Assert.True(vm.CanGo, vm.ErrorText);
            GameProfile? finished = null;
            vm.Finished += p => finished = p;

            Sta.Wait(vm.GoCommand.ExecuteAsync(null));

            var target = Path.Combine(_data, ClientProfileLayout.ContainerName, "Основной (копия)");
            Assert.NotNull(finished);
            Assert.Equal("Основной (копия)", finished!.Name);
            Assert.Equal(ProfileKind.Client, finished.Kind);
            Assert.Equal(target, finished.DataDir);
            Assert.True(File.Exists(Path.Combine(target, "Mods", "carryon_1.8.0.zip")));
            Assert.True(File.Exists(Path.Combine(target, "ModConfig", "carryon.json")));
            Assert.True(File.Exists(Path.Combine(target, "Saves", "Дом.vcdbs")));
            Assert.False(Directory.Exists(Path.Combine(target, "Logs")));
            var paths = JObject.Parse(File.ReadAllText(Path.Combine(target, "clientsettings.json")))["stringListSettings"]!["modPaths"]!;
            Assert.Equal(new[] { "Mods", Path.Combine(target, "Mods") }, paths.Select(p => p.ToString()));

            // ещё раз в ту же папку нельзя: она уже не пустая — настоящий текст ошибки плана
            vm.TargetDir = target + " "; // та же папка (поле уже с ней — без правки план не пересчитать)
            Settle(vm);
            Assert.Equal(Loc.T("clone.errNotEmpty", target), vm.ErrorText);
            Assert.False(vm.CanGo);
        });
    }

    /// <summary>Платформа теста: ручной таймер паузы, выбор папки по заготовке, журнал сбоев в список.</summary>
    private sealed class FakePlatform : IUiPlatform
    {
        public ManualTimer Timer { get; private set; } = new();
        public string? Folder { get; set; }
        public string? FolderTitle { get; private set; }
        public string? FolderStart { get; private set; }
        public List<Exception> CrashLogs { get; } = [];

        public IUiTimer CreateTimer(TimeSpan interval) => Timer = new ManualTimer { Interval = interval };

        public string? PickFolder(string title, string? initialFolder)
        {
            FolderTitle = title;
            FolderStart = initialFolder;
            return Folder;
        }

        public string WriteCrashLog(Exception ex)
        {
            CrashLogs.Add(ex);
            return "";
        }

        public Answer Ask(string text, AskButtons buttons, Answer? byDefault = null, bool warning = false) => throw new InvalidOperationException(text);
        public void Notify(string text, NoticeKind kind) => throw new InvalidOperationException(text);
        public bool IsUiThread => true;
        public void Post(Action action) => action();
        public void PostIdle(Action action) => action();
        public void Invoke(Action action) => action();
        public IReadOnlyList<string>? PickFiles(string title, IReadOnlyList<FileFilter> filters, bool multiple) => throw new NotSupportedException();
        public string? PickSaveFile(string title, IReadOnlyList<FileFilter> filters, string suggestedName, string? folder = null, bool confirmOverwrite = true) =>
            throw new NotSupportedException();
        public void SetClipboard(string text) => throw new NotSupportedException();
        public void OpenUrl(string url) => throw new NotSupportedException();
        public void OpenFolder(string dir) => throw new NotSupportedException();
        public void ShowInFolder(string path) => throw new NotSupportedException();
        public void MoveToTrash(string path) => throw new NotSupportedException();
        public void OpenTrash() => throw new NotSupportedException();
        public bool IsMainWindowVisible => false;
        public void RestartAfterClose(string exe) => throw new NotSupportedException();
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

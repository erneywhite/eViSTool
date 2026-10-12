using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;
using Newtonsoft.Json.Linq;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Окно клона серверного профиля и импорта данных сервера: что видно при открытии, подстановка папки по названию,
/// пересчёт плана после паузы с отбрасыванием устаревших ответов, нехватка места, ошибки плана, копирование с ходом,
/// отменой и сбоями — ветка за веткой. Папки данных — во временной папке; пауза — ручной таймер,
/// свободное место, выбор папки и журнал ошибок подменены.
/// </summary>
public sealed class CloneProfileViewModelTests : IDisposable
{
    private const long Plenty = 1L << 40; // «на диске свободно 1 ТБ»

    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-clvm-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly GameProfile _source;
    private readonly ManualTimer _timer = new();
    private readonly List<CloneOptions> _plans = [];
    private readonly List<Exception> _logged = [];
    private long? _free = Plenty;

    public CloneProfileViewModelTests()
    {
        _data = Directory.CreateDirectory(Path.Combine(_root, "VintagestoryData")).FullName;
        Write("Mods/carryon.zip", "mod");
        Write("ModConfig/carryon.json", "{}");
        Write("Playerdata/playerdata.json", "[]");
        Write("Logs/server-main.log", "log");
        Write("Saves/duo.vcdbs", "duo-world");
        Config("serverconfig.json", "Дуо", "Saves/duo.vcdbs");
        _source = new GameProfile { Name = "Сервер", Kind = ProfileKind.Server, GameDir = Path.Combine(_root, "game"), DataDir = _data };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_data, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>Конфиг сервера: мир и где его сохранение; ModPaths — «Mods» (папка игры) и своя папка модов полным путём, как пишет сервер.</summary>
    private void Config(string file, string? world, string save, params string[] moreModPaths) =>
        File.WriteAllText(Path.Combine(_data, file), new JObject
        {
            ["ServerName"] = "Тест",
            ["ModPaths"] = new JArray(new[] { "Mods", Path.Combine(_data, "Mods") }.Concat(moreModPaths).ToArray<object>()),
            ["WorldConfig"] = world is null ? new JObject { ["SaveFileLocation"] = save } : new JObject { ["WorldName"] = world, ["SaveFileLocation"] = save },
        }.ToString());

    /// <summary>Подмены: ручной таймер паузы, план — настоящий, но с записью условий, своё «свободное место», журнал — в список.</summary>
    private CloneProfileServices Services(Func<GameProfile, CloneOptions, ClonePlan>? plan = null) => CloneProfileServices.Default with
    {
        Timer = _ => _timer,
        Plan = (s, o) =>
        {
            lock (_plans) _plans.Add(o);
            return (plan ?? ProfileCloner.Plan)(s, o);
        },
        FreeSpace = _ => _free,
        LogError = ex => _logged.Add(ex),
        PickFolder = (_, _) => throw new Xunit.Sdk.XunitException("выбор папки в этом тесте не ожидался"),
    };

    private CloneProfileViewModel Open(string? importName = null, bool running = false, CloneProfileServices? services = null)
    {
        var vm = new CloneProfileViewModel(_source, running, importName, services ?? Services());
        Sta.Wait(vm.Planning);
        Sta.Wait(vm.BackupsCounting);
        return vm;
    }

    private int PlanCount
    {
        get { lock (_plans) return _plans.Count; }
    }

    private CloneOptions LastPlan
    {
        get { lock (_plans) return _plans[^1]; }
    }

    /// <summary>Пауза прошла: пересчёт плана — и дождаться его.</summary>
    private void Pause(CloneProfileViewModel vm)
    {
        Assert.True(_timer.IsEnabled, "после правки пауза перед пересчётом должна идти");
        _timer.Fire();
        Sta.Wait(vm.Planning);
    }

    private static string SummaryOf(ClonePlan plan, string free) =>
        Loc.T("clone.summary", plan.Files.Count, Sizes.Format(plan.TotalBytes), free)
        + ProfileCopyText.ModsText(plan.ModSources, plan.ModNotes) + " " + Loc.T("clone.summarySkipped");

    // ---- открытие

    [Fact]
    public void Clone_Opens_WithCopyName_TargetByName_AndPlansAtOnce() => Sta.Run(() =>
    {
        var vm = Open();

        Assert.False(vm.IsImport);
        Assert.Equal(Loc.T("clone.title"), vm.Title);
        Assert.Equal(Loc.T("clone.heading", "Сервер"), vm.Heading);
        Assert.Equal(Loc.T("clone.copySuffix", "Сервер"), vm.Name);
        Assert.Equal(vm.Name, vm.WorldName);
        Assert.Equal(ProfileCloner.SuggestTargetDir(_data, vm.Name), vm.TargetDir);
        Assert.Equal(Path.Combine(_data, ServerProfileLayout.ContainerName, vm.Name), vm.TargetDir);
        Assert.Equal(Loc.T("clone.modsSharedHint", Path.Combine(_data, "Mods")), vm.SharedModsHint);

        // по умолчанию — тот же мир, без бэкапов, моды общие
        Assert.False(vm.NewWorld);
        Assert.False(vm.IncludeBackups);
        Assert.True(vm.ShareMods);
        Assert.False(vm.IsServerRunning);
        Assert.False(vm.IsCopying);
        Assert.True(vm.IsFormEnabled);

        // план посчитан сразу, без паузы
        Assert.False(_timer.IsEnabled);
        Assert.Equal(1, PlanCount);
        var options = LastPlan;
        Assert.Equal(vm.Name, options.Name);
        Assert.Equal(vm.TargetDir, options.TargetDir);
        Assert.Equal(ProfileCloner.MainConfig, options.ConfigFile);
        Assert.True(options.ShareMods);
        Assert.False(options.NewWorld);
        Assert.False(options.IncludeBackups);

        var plan = ProfileCloner.Plan(_source, options);
        Assert.Equal(SummaryOf(plan, Sizes.Format(Plenty)), vm.Summary);
        Assert.Equal("", vm.ErrorText);
        Assert.False(vm.HasError);
        Assert.True(vm.CanGo);
        Assert.True(vm.GoCommand.CanExecute(null));
    });

    [Fact]
    public void Import_Opens_WithGivenName_AndImportTitles() => Sta.Run(() =>
    {
        var vm = Open(importName: "Сервер 2");

        Assert.True(vm.IsImport);
        Assert.Equal(Loc.T("newprofile.titleServer"), vm.Title);
        Assert.Equal(Loc.T("clone.headingImport", _data), vm.Heading);
        Assert.Equal("Сервер 2", vm.Name);
        Assert.Equal("Сервер 2", vm.WorldName);
        Assert.Equal(Path.Combine(_data, ServerProfileLayout.ContainerName, "Сервер 2"), vm.TargetDir);
        Assert.True(vm.CanGo);
    });

    [Fact]
    public void ServerRunning_PlanIsShown_ButCloneIsNotAllowed() => Sta.Run(() =>
    {
        var vm = Open(running: true);

        Assert.True(vm.IsServerRunning);
        Assert.StartsWith(Loc.T("clone.summary", "", "", "").Split(':')[0], vm.Summary);
        Assert.Equal("", vm.ErrorText);
        Assert.False(vm.CanGo);
        Assert.False(vm.GoCommand.CanExecute(null));
    });

    // ---- конфиги (какой мир взять)

    [Fact]
    public void SeveralConfigs_OfferChoice_MainFirst_WithWorldAndSaveSize() => Sta.Run(() =>
    {
        Config("serverconfig-b.json", "Большой", "Saves/big.vcdbs");     // сохранения нет
        Config("serverconfig-c.json", null, "Saves/duo.vcdbs");          // мир без названия
        var vm = Open();

        Assert.True(vm.HasConfigChoice);
        Assert.Equal(["serverconfig.json", "serverconfig-b.json", "serverconfig-c.json"], vm.Configs.Select(c => c.Value.FileName));
        var size = Sizes.Format(new FileInfo(Path.Combine(_data, "Saves", "duo.vcdbs")).Length);
        Assert.Equal(Loc.T("clone.configItem", "serverconfig.json", "Дуо", size), vm.Configs[0].Title);
        Assert.Equal(Loc.T("clone.configItem", "serverconfig-b.json", "Большой", Loc.T("clone.configNoSave")), vm.Configs[1].Title);
        Assert.Equal(Loc.T("clone.configItem", "serverconfig-c.json", "?", size), vm.Configs[2].Title);
        Assert.Same(vm.Configs[0], vm.SelectedConfig);

        // другой мир — пересчёт после паузы, в плане — его конфиг
        vm.SelectedConfig = vm.Configs[1];
        Assert.False(vm.CanGo);
        Pause(vm);
        Assert.Equal("serverconfig-b.json", LastPlan.ConfigFile);
        Assert.True(vm.CanGo);
    });

    [Fact]
    public void SingleConfig_NoChoice_ButItIsUsed_EvenWithOtherName() => Sta.Run(() =>
    {
        File.Move(Path.Combine(_data, "serverconfig.json"), Path.Combine(_data, "serverconfig-old.json"));
        var vm = Open();

        Assert.False(vm.HasConfigChoice);
        Assert.Equal("serverconfig-old.json", vm.SelectedConfig?.Value.FileName);
        Assert.Equal("serverconfig-old.json", LastPlan.ConfigFile);
    });

    [Fact]
    public void NoConfigs_NoChoice_PlanUsesMainConfigName() => Sta.Run(() =>
    {
        File.Delete(Path.Combine(_data, "serverconfig.json"));
        var vm = Open();

        Assert.False(vm.HasConfigChoice);
        Assert.Empty(vm.Configs);
        Assert.Null(vm.SelectedConfig);
        Assert.Equal(ProfileCloner.MainConfig, LastPlan.ConfigFile);
        Assert.True(vm.CanGo); // сервер создаст свой конфиг — клонировать можно
    });

    // ---- папка нового профиля

    [Fact]
    public void Name_MovesTarget_UntilTargetIsEditedByHand() => Sta.Run(() =>
    {
        var vm = Open();

        vm.Name = "Дуо 2";
        Assert.Equal(Path.Combine(_data, ServerProfileLayout.ContainerName, "Дуо 2"), vm.TargetDir);

        var own = Path.Combine(_root, "мой профиль");
        vm.TargetDir = own;
        vm.Name = "Дуо 3";
        Assert.Equal(own, vm.TargetDir); // правленную руками папку название больше не двигает
        Pause(vm);
        Assert.Equal("Дуо 3", LastPlan.Name);
        Assert.Equal(own, LastPlan.TargetDir);
    });

    [Fact]
    public void Browse_StartsFromNearestExistingFolder_AndPickedFolderStopsFollowingName() => Sta.Run(() =>
    {
        string? start = null;
        var picked = Path.Combine(_root, "выбранная");
        var vm = Open(services: Services() with
        {
            PickFolder = (title, s) =>
            {
                Assert.Equal(Loc.T("clone.pickTarget"), title);
                start = s;
                return picked;
            },
        });
        // предложенной папки ещё нет — начать с ближайшей существующей над ней
        vm.BrowseCommand.Execute(null);
        Assert.Equal(_data, start);
        Assert.Equal(picked, vm.TargetDir);

        vm.Name = "Другое";
        Assert.Equal(picked, vm.TargetDir);
    });

    [Fact]
    public void Browse_Cancelled_ChangesNothing_AndEmptyFieldStartsNowhere() => Sta.Run(() =>
    {
        var starts = new List<string?>();
        var vm = Open(services: Services() with
        {
            PickFolder = (_, s) =>
            {
                starts.Add(s);
                return null;
            },
        });
        var target = vm.TargetDir;
        vm.BrowseCommand.Execute(null);
        Assert.Equal(target, vm.TargetDir);
        Assert.False(_timer.IsEnabled); // ничего не поменялось — и пересчёта нет

        vm.TargetDir = "  ";
        vm.BrowseCommand.Execute(null);
        Assert.Equal("", starts[^1]);
    });

    [Fact]
    public void SourceWithoutDataFolder_NoTargetSuggestion_AndPlanErrorShown() => Sta.Run(() =>
    {
        var source = new GameProfile { Name = "Пустой", Kind = ProfileKind.Server, DataDir = null };
        var vm = new CloneProfileViewModel(source, false, null, Services());
        Sta.Wait(vm.Planning);

        Assert.Equal("", vm.TargetDir);
        Assert.Empty(vm.Configs);
        Assert.Equal(Loc.T("clone.modsSharedHint", "—"), vm.SharedModsHint);
        vm.Name = "Новое";
        Assert.Equal("", vm.TargetDir); // подставлять не от чего
        Pause(vm);
        Assert.Equal(Loc.T("clone.summarySkipped"), vm.Summary);
        Assert.Equal(Loc.T("clone.errNoSource", (string?)null), vm.ErrorText);
        Assert.False(vm.CanGo);
    });

    // ---- пересчёт плана

    [Fact]
    public void Edits_TurnGoOff_AndQuickEditsGiveOnePlanAfterPause() => Sta.Run(() =>
    {
        var vm = Open();
        Assert.True(vm.CanGo);

        vm.Name = "А";
        vm.Name = "Аб";
        vm.ShareMods = false;
        vm.NewWorld = true;
        vm.IncludeBackups = true;
        Assert.False(vm.CanGo);
        Assert.False(vm.GoCommand.CanExecute(null));
        Assert.Equal(1, PlanCount); // пока пауза идёт, план не считается
        // каждая правка начинает паузу заново; название двигает и папку — это две правки
        Assert.Equal(7, _timer.Starts);

        Pause(vm);
        Assert.Equal(2, PlanCount);
        var options = LastPlan;
        Assert.Equal("Аб", options.Name);
        Assert.False(options.ShareMods);
        Assert.True(options.NewWorld);
        Assert.True(options.IncludeBackups);
        Assert.True(vm.CanGo);
    });

    [Fact]
    public void Options_AreTrimmed() => Sta.Run(() =>
    {
        var vm = Open();
        vm.Name = "  Дуо  ";
        vm.TargetDir = "  " + Path.Combine(_root, "duo") + "  ";
        vm.WorldName = "  Мир  ";
        Pause(vm);

        Assert.Equal("Дуо", LastPlan.Name);
        Assert.Equal(Path.Combine(_root, "duo"), LastPlan.TargetDir);
        Assert.Equal("Мир", LastPlan.WorldName);
    });

    [Fact]
    public void WorldName_DoesNotRecalculate() => Sta.Run(() =>
    {
        var vm = Open();
        vm.WorldName = "Другой мир";
        Assert.False(_timer.IsEnabled);
        Assert.True(vm.CanGo);
        Assert.Equal(1, PlanCount);
    });

    [Fact]
    public void StaleAnswer_IsDropped() => Sta.Run(() =>
    {
        var release = new ManualResetEventSlim();
        var calls = 0;
        var vm = Open(services: Services((s, o) =>
        {
            // второй план (после первой правки) долгий: его ответ придёт, когда условия уже другие
            if (Interlocked.Increment(ref calls) == 2) release.Wait();
            return ProfileCloner.Plan(s, o);
        }));
        vm.Name = "Медленный";
        _timer.Fire();
        var slow = vm.Planning;
        Assert.False(slow.IsCompleted);

        vm.Name = "Быстрый";
        Assert.False(vm.CanGo);
        release.Set();
        Sta.Wait(slow);
        Assert.False(vm.CanGo); // ответ устарел — не показан, «Клонировать» по-прежнему ждёт

        Pause(vm);
        Assert.Equal("Быстрый", LastPlan.Name);
        Assert.True(vm.CanGo);
    });

    [Fact]
    public void PlanRefusal_ShowsReason_SummaryOnlySkipped() => Sta.Run(() =>
    {
        var vm = Open();
        Directory.CreateDirectory(Path.Combine(_root, "busy"));
        File.WriteAllText(Path.Combine(_root, "busy", "x.txt"), "x");
        vm.TargetDir = Path.Combine(_root, "busy");
        Pause(vm);

        Assert.Equal(Loc.T("clone.summarySkipped"), vm.Summary);
        Assert.Equal(Loc.T("clone.errNotEmpty", Path.Combine(_root, "busy")), vm.ErrorText);
        Assert.True(vm.HasError);
        Assert.False(vm.CanGo);
        Assert.Empty(_logged);
    });

    [Fact]
    public void EmptyName_IsRefused() => Sta.Run(() =>
    {
        var vm = Open();
        vm.TargetDir = Path.Combine(_root, "x");
        vm.Name = "   ";
        Pause(vm);
        Assert.Equal(Loc.T("clone.errName"), vm.ErrorText);
        Assert.False(vm.CanGo);
    });

    [Fact]
    public void BrokenConfig_IsShownAsReason_NotLogged() => Sta.Run(() =>
    {
        Write("serverconfig-broken.json", "{ это не json");
        var vm = Open();
        vm.SelectedConfig = vm.Configs.Single(c => c.Value.FileName == "serverconfig-broken.json");
        Pause(vm);

        Assert.True(vm.HasError);
        Assert.False(vm.CanGo);
        Assert.Empty(_logged);
    });

    [Fact]
    public void UnexpectedPlanError_IsLogged_AndShown() => Sta.Run(() =>
    {
        var vm = Open(services: Services((s, o) => o.Name == "Сбой" ? throw new NullReferenceException("внезапно") : ProfileCloner.Plan(s, o)));
        vm.Name = "Сбой";
        Pause(vm);

        Assert.Equal("внезапно", vm.ErrorText);
        Assert.Equal(Loc.T("clone.summarySkipped"), vm.Summary);
        Assert.IsType<NullReferenceException>(Assert.Single(_logged));
        Assert.False(vm.CanGo);
    });

    // ---- свободное место

    [Fact]
    public void NotEnoughSpace_BlocksClone() => Sta.Run(() =>
    {
        _free = 1;
        var vm = Open();
        Assert.Equal(SummaryOf(ProfileCloner.Plan(_source, LastPlan), Sizes.Format(1)), vm.Summary);
        Assert.Equal(Loc.T("clone.noSpace"), vm.ErrorText);
        Assert.False(vm.CanGo);

        _free = Plenty;
        vm.Name = "Ещё";
        Pause(vm);
        Assert.Equal("", vm.ErrorText);
        Assert.True(vm.CanGo);
    });

    [Fact]
    public void UnknownFreeSpace_ShowsDash_AndDoesNotBlock() => Sta.Run(() =>
    {
        _free = null;
        var vm = Open();
        Assert.Equal(SummaryOf(ProfileCloner.Plan(_source, LastPlan), "—"), vm.Summary);
        Assert.Equal("", vm.ErrorText);
        Assert.True(vm.CanGo);
    });

    // ---- моды

    [Fact]
    public void OwnMods_SummaryTellsWhereModsComeFrom() => Sta.Run(() =>
    {
        // одна из папок модов пропала — в сводке строкой ниже
        Config("serverconfig.json", "Дуо", "Saves/duo.vcdbs", Path.Combine(_root, "пропавшие моды"));
        var vm = Open();
        vm.ShareMods = false;
        Pause(vm);

        var plan = ProfileCloner.Plan(_source, LastPlan);
        Assert.Equal([Path.Combine(_data, "Mods")], plan.ModSources);
        Assert.NotEmpty(plan.ModNotes);
        Assert.Equal(SummaryOf(plan, Sizes.Format(Plenty)), vm.Summary);
        Assert.Contains("• " + plan.ModNotes[0], vm.Summary);
        Assert.Contains(Loc.T("clone.modsFrom", string.Join(", ", plan.ModSources)), vm.Summary);
    });

    // ---- резервные копии

    [Fact]
    public void Backups_Found_OfferCheckbox_OnlyForSameWorld() => Sta.Run(() =>
    {
        Write("Backups/a.vcdbs", new string('b', 3000));
        Write("BackupSaves/x/b.vcdbs", new string('c', 1000));
        var vm = Open();

        Assert.True(vm.HasBackups);
        Assert.Equal(Loc.T("clone.backups", Sizes.Format(4000)), vm.BackupsText);
        Assert.True(vm.ShowBackups);
        Assert.False(vm.IncludeBackups); // по умолчанию не копируются
        Assert.False(_timer.IsEnabled);  // нашлись — это не правка, план не пересчитывается

        vm.NewWorld = true;
        Assert.False(vm.ShowBackups);
        vm.NewWorld = false;
        Assert.True(vm.ShowBackups);

        vm.IncludeBackups = true;
        Pause(vm);
        Assert.True(LastPlan.IncludeBackups);
    });

    [Fact]
    public void NoBackups_NoCheckbox() => Sta.Run(() =>
    {
        Directory.CreateDirectory(Path.Combine(_data, "Backups")); // пустая — не в счёт
        var vm = Open();
        Assert.False(vm.HasBackups);
        Assert.False(vm.ShowBackups);
        Assert.Equal("", vm.BackupsText);
    });

    [Fact]
    public void BackupBytes_SumsBothFolders_AndToleratesMissingDataDir()
    {
        Write("Backups/a.vcdbs", "12345");
        Write("BackupSaves/deep/b.vcdbs", "123");
        Assert.Equal(8, CloneProfileViewModel.BackupBytes(_data));
        Assert.Equal(0, CloneProfileViewModel.BackupBytes(null));
        Assert.Equal(0, CloneProfileViewModel.BackupBytes(Path.Combine(_root, "нет такой")));
    }

    // ---- копирование

    [Fact]
    public void Go_CopiesWithCurrentWorldName_ReportsProgress_AndFinishes() => Sta.Run(() =>
    {
        ClonePlan? applied = null;
        var vm = Open(services: Services() with
        {
            Apply = async (plan, progress, ct) =>
            {
                applied = plan;
                return await ProfileCloner.ApplyAsync(plan, progress, ct);
            },
        });
        vm.NewWorld = true;
        vm.TargetDir = Path.Combine(_root, "клон");
        Pause(vm);
        vm.WorldName = "  Новый мир  "; // после расчёта плана — пересчёта нет, но в копию идёт оно
        var progress = new List<double>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Progress)) progress.Add(vm.Progress);
        };
        GameProfile? finished = null;
        vm.Finished += p => finished = p;

        Sta.Wait(vm.GoCommand.ExecuteAsync(null));
        Sta.WaitFor(() => progress.Count > 0 && progress[^1] >= 1); // ход приходит через очередь окна

        Assert.Equal("Новый мир", applied!.Options.WorldName);
        Assert.NotNull(finished);
        Assert.Same(finished, vm.Result);
        Assert.Equal(vm.Name, finished!.Name);
        Assert.Equal(Path.Combine(_root, "клон"), finished.DataDir);
        Assert.True(File.Exists(Path.Combine(_root, "клон", "ModConfig", "carryon.json")));
        Assert.Equal("Новый мир", (string?)JObject.Parse(File.ReadAllText(Path.Combine(_root, "клон", "serverconfig.json")))["WorldConfig"]!["WorldName"]);
        Assert.StartsWith(Loc.T("clone.copying", "", "", "").Split('…')[0], vm.ProgressText);
        // окно закрывается: форма так и остаётся выключенной, отменять нечего
        Assert.True(vm.IsCopying);
        Assert.False(vm.CanGo);
        Assert.False(vm.CancelCopy());
    });

    [Fact]
    public void Go_WhileCopying_FormOff_ProgressShown_AndCancelReturnsForm() => Sta.Run(() =>
    {
        var started = new TaskCompletionSource();
        long total = 0;
        var vm = Open(services: Services() with
        {
            Apply = async (plan, progress, ct) =>
            {
                total = plan.TotalBytes;
                progress.Report(new CloneProgress(5, 10, "Saves/duo.vcdbs"));
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new Xunit.Sdk.XunitException("не отменили");
            },
        });
        Assert.False(vm.CancelCopy()); // копирования нет — окно можно закрыть

        var go = vm.GoCommand.ExecuteAsync(null);
        Sta.WaitFor(() => started.Task.IsCompleted && vm.ProgressText.Length > 0);
        Assert.True(vm.IsCopying);
        Assert.False(vm.IsFormEnabled);
        Assert.False(vm.CanGo);
        Assert.Equal(5.0 / total, vm.Progress); // доля — от объёма плана
        Assert.Equal(Loc.T("clone.copying", "Saves/duo.vcdbs", Sizes.Format(5), Sizes.Format(10)), vm.ProgressText);

        Assert.True(vm.CancelCopy()); // идёт — прервать, окно остаётся
        Sta.Wait(go);
        Sta.Wait(vm.Planning);

        Assert.False(vm.IsCopying);
        Assert.True(vm.IsFormEnabled);
        Assert.Null(vm.Result);
        Assert.Equal(Loc.T("clone.cancelled"), vm.ErrorText); // причина видна и после нового расчёта плана
        Assert.True(vm.CanGo);
        Assert.Empty(_logged);

        // следующая правка убирает причину
        vm.Name = "Снова";
        Pause(vm);
        Assert.Equal("", vm.ErrorText);
    });

    [Fact]
    public void Go_RestartsProgress_FromZero() => Sta.Run(() =>
    {
        var attempt = 0;
        var vm = Open(services: Services() with
        {
            Apply = (plan, progress, ct) =>
            {
                if (++attempt == 1)
                {
                    progress.Report(new CloneProgress(9, 10, "a"));
                    return Task.FromException<GameProfile>(new IOException("нет"));
                }
                return Task.FromException<GameProfile>(new IOException("снова нет"));
            },
        });
        Sta.Wait(vm.GoCommand.ExecuteAsync(null));
        Sta.WaitFor(() => vm.Progress > 0);
        Sta.Wait(vm.Planning);
        double? first = null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Progress)) first ??= vm.Progress;
        };
        Sta.Wait(vm.GoCommand.ExecuteAsync(null));
        Assert.Equal(0, first);
        Assert.Equal("", vm.ProgressText);
    });

    [Fact]
    public void Go_ExpectedFailure_ShowsReason_FormBack_NotLogged() => Sta.Run(() =>
    {
        Exception[] errors =
        [
            new IOException("диск отвалился"),
            new UnauthorizedAccessException("нет прав"),
            new Newtonsoft.Json.JsonReaderException("битый конфиг"),
            new InvalidOperationException("в папке что-то появилось"),
        ];
        foreach (var error in errors)
        {
            var vm = Open(services: Services() with { Apply = (_, _, _) => Task.FromException<GameProfile>(error) });
            Sta.Wait(vm.GoCommand.ExecuteAsync(null));
            Sta.Wait(vm.Planning);

            Assert.Equal(Loc.T("clone.failed", error.Message), vm.ErrorText);
            Assert.False(vm.IsCopying);
            Assert.True(vm.CanGo);
            Assert.Null(vm.Result);
        }
        Assert.Empty(_logged);
    });

    [Fact]
    public void Go_WorldBusy_IsRefused_LikeOtherExpectedFailures() => Sta.Run(() =>
    {
        var vm = Open();
        vm.TargetDir = Path.Combine(_root, "клон");
        Pause(vm);
        using (WorldLock.Take(_data, "тест")) // мир занят (сервер, восстановление) — копирование откажет сразу
        {
            Sta.Wait(vm.GoCommand.ExecuteAsync(null));
        }
        Sta.Wait(vm.Planning);

        Assert.StartsWith(Loc.T("clone.failed", "").TrimEnd(), vm.ErrorText);
        Assert.False(Directory.Exists(Path.Combine(_root, "клон")));
        Assert.Empty(_logged);
        Assert.True(vm.CanGo);
    });

    [Fact]
    public void Go_UnexpectedFailure_IsLogged_AndFormReturns() => Sta.Run(() =>
    {
        var vm = Open(services: Services() with { Apply = (_, _, _) => Task.FromException<GameProfile>(new NullReferenceException("ой")) });
        Sta.Wait(vm.GoCommand.ExecuteAsync(null));
        Sta.Wait(vm.Planning);

        Assert.Equal(Loc.T("clone.failed", "ой"), vm.ErrorText);
        Assert.IsType<NullReferenceException>(Assert.Single(_logged));
        Assert.False(vm.IsCopying);
        Assert.True(vm.IsFormEnabled);
    });

    [Fact]
    public void Notice_WinsOverPlanError_ButNoSpaceWinsOverNotice() => Sta.Run(() =>
    {
        var failPlan = false;
        var vm = Open(services: Services((s, o) => failPlan ? throw new IOException("план не посчитать") : ProfileCloner.Plan(s, o)) with
        {
            Apply = (_, _, _) => Task.FromException<GameProfile>(new OperationCanceledException()),
        });

        // копирование прервано, а план после него не посчитался — видна причина прерывания
        failPlan = true;
        Sta.Wait(vm.GoCommand.ExecuteAsync(null));
        Sta.Wait(vm.Planning);
        Assert.Equal(Loc.T("clone.cancelled"), vm.ErrorText);
        Assert.Equal(Loc.T("clone.summarySkipped"), vm.Summary);
        Assert.False(vm.CanGo);

        // без причины прерывания — ошибка плана
        vm.Name = "Ещё";
        Pause(vm);
        Assert.Equal("план не посчитать", vm.ErrorText);

        // прервано, но места нет — сначала про место
        failPlan = false;
        vm.Name = "Снова";
        Pause(vm);
        Assert.True(vm.CanGo);
        _free = 1;
        Sta.Wait(vm.GoCommand.ExecuteAsync(null));
        Sta.Wait(vm.Planning);
        Assert.Equal(Loc.T("clone.noSpace"), vm.ErrorText);
        Assert.False(vm.CanGo);
    });

    [Fact]
    public void Go_WithoutReadyPlan_DoesNothing() => Sta.Run(() =>
    {
        var applied = 0;
        var vm = Open(services: Services() with
        {
            Apply = (_, _, _) =>
            {
                applied++;
                return Task.FromException<GameProfile>(new IOException("x"));
            },
        });
        vm.Name = "Правка"; // план устарел, новый ещё не посчитан
        Sta.Wait(vm.GoCommand.ExecuteAsync(null));
        Assert.Equal(0, applied);
        Assert.False(vm.IsCopying);
    });

    [Fact]
    public void PauseEndingDuringCopy_DoesNotRecalculate() => Sta.Run(() =>
    {
        var release = new TaskCompletionSource<GameProfile>();
        var vm = Open(services: Services() with { Apply = (_, _, _) => release.Task });
        var go = vm.GoCommand.ExecuteAsync(null);
        Sta.WaitFor(() => vm.IsCopying);

        vm.WorldName = "x";
        vm.Name = "во время копирования"; // в окне форма выключена; модель всё равно не считает план поверх копирования
        var before = PlanCount;
        Pause(vm);
        Assert.Equal(before, PlanCount);

        release.SetResult(new GameProfile { Name = "готово" });
        Sta.Wait(go);
        Assert.Equal("готово", vm.Result?.Name);
    });

    /// <summary>Таймер паузы, который срабатывает, когда скажет тест.</summary>
    private sealed class ManualTimer : IUiTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsEnabled { get; private set; }
        public int Starts { get; private set; }
        public event EventHandler? Tick;

        public void Start()
        {
            IsEnabled = true;
            Starts++;
        }

        public void Stop() => IsEnabled = false;

        public void Fire()
        {
            if (IsEnabled) Tick?.Invoke(this, EventArgs.Empty);
        }
    }
}

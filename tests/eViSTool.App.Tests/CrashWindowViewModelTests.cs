using System.Runtime.InteropServices;
using eViSTool.Core.Diagnostics;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.App.Tests;

/// <summary>
/// Окно вылета игры или падения сервера: тексты (виновник, как нашли, причина, подробности), какие кнопки видны,
/// выключение мода (удачно, после выхода из игры, с ошибкой — и можно ли нажать ещё раз), переходы в программе,
/// копирование. Вылеты выдуманные; программа вокруг окна (главное окно, каталог, буфер) и выключение подменены.
/// </summary>
public sealed class CrashWindowViewModelTests
{
    private static readonly GameProfile Client = new() { Id = "client-1", Name = "Игра", Kind = ProfileKind.Client };
    private static readonly GameProfile Server = new() { Id = "server-1", Name = "Сервер", Kind = ProfileKind.Server };

    private static readonly string[] Frames = ["PrimitiveSurvival.ModSystem.BEFishBasket.OnTick", "Vintagestory.Client.NoObf.ClientMain.MainGameLoop"];

    /// <summary>Программа вокруг окна: что окно у неё просило, по порядку.</summary>
    private sealed class Host : ICrashWindowHost
    {
        public List<string> Calls { get; } = [];
        public List<string> Copied { get; } = [];
        /// <summary>Буфер обмена занят: чем он ответит.</summary>
        public Exception? ClipboardFails { get; set; }

        public void SwitchTo(GameProfile profile, int tab) => Calls.Add($"switch {profile.Id} {tab}");
        public void SearchMods(string text) => Calls.Add($"search {text}");
        public Task ShowInCatalogAsync(string modId, string? name)
        {
            Calls.Add($"catalog {modId} {name}");
            return Task.CompletedTask;
        }
        public void ActivateMainWindow() => Calls.Add("activate");
        public void SetClipboard(string text)
        {
            if (ClipboardFails is { } ex) throw ex;
            Copied.Add(text);
        }
    }

    /// <summary>Выключатель без игры и диска: что и где выключали.</summary>
    private sealed class Disables
    {
        public bool GameRunning { get; set; }
        public int TargetCalls { get; private set; }
        public List<string> Paths { get; } = [];
        public List<Func<Task>> Deferred { get; } = [];

        public ModDisabler Disabler() => new(
            _ => GameRunning, (_, action) => Deferred.Add(action), _ => { },
            p => { TargetCalls++; return new ModTarget(p, null); },
            (_, path) => { Paths.Add(path); return Task.CompletedTask; });
    }

    private static ModPrint Print(string id = "primitivesurvival", string name = "Primitive Survival", string version = "3.7.6") =>
        new(id, name, version, $@"C:\VintagestoryData\Mods\{id}_{version}.zip", new HashSet<string>(), new HashSet<string>());

    private static CrashFinding Finding(ModCulprit? culprit, GameExitKind kind = GameExitKind.Crashed, string reason = "Game crashed",
        int errors = 1, string? error = "System.NullReferenceException: Object reference not set to an instance of an object.") =>
        new(kind, reason, error, Frames, culprit, errors);

    private static CrashView View(string? modId = "carryon", CulpritSource? source = CulpritSource.Stack, bool installed = true,
        string reason = "Game crashed", int errors = 1, string? error = "boom", IReadOnlyList<string>? stack = null) =>
        new("Игра вылетела", "Игра", modId, modId is null ? null : "Carry On", modId is null ? null : "1.8.0", source, installed, reason,
            errors, error, stack ?? Frames);

    private static Func<Task<ModDisableResult>> Ok(string text = "выключен") => () => Task.FromResult(new ModDisableResult(text, false));

    private static CrashWindowViewModel Model(CrashView view, Func<Task<ModDisableResult>>? disable = null, Host? host = null, Action? show = null) =>
        new(view, disable, show ?? (() => { }), "Найти", host ?? new Host());

    // ---- тексты

    [Theory]
    [InlineData(CulpritSource.GameReport, "crash.howGameReport")]
    [InlineData(CulpritSource.Stack, "crash.howStack")]
    [InlineData(CulpritSource.Message, "crash.howMessage")]
    [InlineData(CulpritSource.Translation, "crash.howTranslation")]
    public void Culprit_And_HowFound(CulpritSource source, string howKey)
    {
        var vm = Model(View(source: source));
        Assert.Equal("Игра вылетела", vm.Title);
        Assert.Equal(Loc.T("crash.profile", "Игра"), vm.ProfileText);
        Assert.Equal(Loc.T("crash.culprit", "Carry On", "1.8.0"), vm.CulpritText);
        Assert.Equal(Loc.T(howKey), vm.HowText);
        Assert.Equal("Найти", vm.ShowLabel);
    }

    // источник не записан (старый агент) — как и в окне на WPF, «строка перевода»
    [Fact]
    public void UnknownSource_FallsBackToTranslation()
    {
        Assert.Equal(Loc.T("crash.howTranslation"), Model(View(source: null)).HowText);
    }

    // имени и версии нет (сервер назвал только modid) — показываем modid, версию пропускаем
    [Fact]
    public void Culprit_WithoutNameAndVersion_ShowsModId()
    {
        var view = View() with { ModName = null, ModVersion = null };
        Assert.Equal(Loc.T("crash.culprit", "carryon", ""), Model(view).CulpritText);
    }

    [Fact]
    public void Culprit_NotInstalled_IsSaid_AndCannotBeDisabled()
    {
        var vm = Model(View(source: CulpritSource.Message, installed: false), Ok());
        Assert.Equal(Loc.T("crash.howMessage") + " " + Loc.T("crash.notInstalled"), vm.HowText);
        Assert.False(vm.CanDisable); // выключать нечего, даже если путь известен
        Assert.False(vm.DisableCommand.CanExecute(null));
        Assert.True(vm.HasCatalogLink); // а страница мода в каталоге есть
    }

    [Fact]
    public void NoCulprit_UnknownText_NoDisable_NoCatalog()
    {
        var vm = Model(View(modId: null, source: null, installed: false));
        Assert.Equal(Loc.T("crash.unknown"), vm.CulpritText);
        Assert.Equal(Loc.T("crash.unknownHint"), vm.HowText);
        Assert.False(vm.CanDisable);
        Assert.False(vm.HasCatalogLink);
    }

    [Theory]
    [InlineData("Game crashed", 1, "Game crashed", false)]
    [InlineData("Server crashed. ", 1, "Server crashed", false)] // точку и пробелы в конце убираем: точку ставит сам текст
    [InlineData("Too many errors...", 37, "Too many errors", true)]
    [InlineData("Too many errors", 2, "Too many errors", true)]
    public void Reason_TrimmedDot_AndErrorCountWhenMoreThanOne(string reason, int errors, string shown, bool withCount)
    {
        var vm = Model(View(reason: reason, errors: errors));
        Assert.Equal(Loc.T("crash.reason", shown) + (withCount ? " " + Loc.T("crash.errorCount", errors) : ""), vm.ReasonText);
    }

    [Fact]
    public void Details_ErrorThenStack()
    {
        var nl = Environment.NewLine;
        Assert.Equal($"boom{nl}   at {Frames[0]}{nl}   at {Frames[1]}", Model(View()).Details);
        // ошибки нет — первая строка пустая, стек как есть
        Assert.Equal($"{nl}   at {Frames[0]}{nl}   at {Frames[1]}", Model(View(error: null)).Details);
        Assert.Equal("boom", Model(View(stack: [])).Details);
    }

    // ---- кнопки

    [Fact]
    public void Disable_VisibleOnlyWithDisableAndInstalledMod()
    {
        Assert.True(Model(View(), Ok()).CanDisable);
        Assert.False(Model(View(), disable: null).CanDisable);
        Assert.False(Model(View(installed: false), Ok()).CanDisable);
    }

    [Fact]
    public async Task Disable_Success_ShowsText_AndStaysUnavailable()
    {
        var vm = Model(View(), Ok("Мод выключен"));
        Assert.True(vm.DisableCommand.CanExecute(null));

        await vm.DisableCommand.ExecuteAsync(null);

        Assert.Equal("Мод выключен", vm.Status);
        Assert.False(vm.DisableCommand.CanExecute(null)); // уже выключен — второй раз не нужно
    }

    [Fact]
    public async Task Disable_Failure_ShowsText_AndCanRetry()
    {
        var vm = Model(View(), () => Task.FromResult(new ModDisableResult("Не удалось выключить: занят", Failed: true)));

        await vm.DisableCommand.ExecuteAsync(null);

        Assert.Equal("Не удалось выключить: занят", vm.Status);
        Assert.True(vm.DisableCommand.CanExecute(null));
    }

    // пока выключается (агент удалённого сервера отвечает не сразу) — второй раз не нажать
    [Fact]
    public async Task Disable_WhileRunning_IsUnavailable()
    {
        var gate = new TaskCompletionSource<ModDisableResult>();
        var vm = Model(View(), () => gate.Task);

        var running = vm.DisableCommand.ExecuteAsync(null);
        Assert.False(vm.DisableCommand.CanExecute(null));
        Assert.Equal("", vm.Status);

        gate.SetResult(new ModDisableResult("готово", false));
        await running;
        Assert.Equal("готово", vm.Status);
        Assert.False(vm.DisableCommand.CanExecute(null));
    }

    [Fact]
    public void Show_RunsJump_ThenActivatesMainWindow()
    {
        var host = new Host();
        var vm = Model(View(), host: host, show: () => host.Calls.Add("jump"));
        vm.ShowCommand.Execute(null);
        Assert.Equal(["jump", "activate"], host.Calls);
    }

    [Fact]
    public void Catalog_OpensModPage_ThenActivatesMainWindow()
    {
        var host = new Host();
        Model(View(), host: host).CatalogCommand.Execute(null);
        Assert.Equal(["catalog carryon Carry On", "activate"], host.Calls);
    }

    [Fact]
    public void Catalog_WithoutCulprit_DoesNothing()
    {
        var host = new Host();
        Model(View(modId: null, source: null), host: host).CatalogCommand.Execute(null);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void Copy_PutsReport_AndSaysSo()
    {
        var host = new Host();
        var vm = Model(View(errors: 3), host: host);

        vm.CopyCommand.Execute(null);

        var nl = Environment.NewLine;
        var expected = $"Игра вылетела — {vm.ProfileText}{nl}{vm.CulpritText}{nl}{vm.ReasonText}{nl}{nl}{vm.Details}";
        Assert.Equal([expected], host.Copied);
        Assert.Equal(Loc.T("crash.copied"), vm.Status);
    }

    // буфер занят другой программой — молча, как в окне на WPF: ни ошибки, ни «скопировано»
    [Fact]
    public void Copy_ClipboardBusy_IsQuiet()
    {
        foreach (var busy in new Exception[] { new COMException("CLIPBRD_E_CANT_OPEN"), new ExternalException("занят"), new TimeoutException() })
        {
            var vm = Model(View(), host: new Host { ClipboardFails = busy });
            vm.CopyCommand.Execute(null);
            Assert.Equal("", vm.Status);
        }
    }

    // ---- окно вылета игры

    [Fact]
    public async Task ForGame_InstalledCulprit_DisablesItsArchive()
    {
        var disables = new Disables();
        var mod = Print();
        var vm = CrashWindowViewModel.ForGame(Client, Finding(new ModCulprit(mod, mod.ModId, mod.Version, CulpritSource.Stack)), disables.Disabler(), new Host());

        Assert.Equal(Loc.T("crash.titleCrashed"), vm.Title);
        Assert.Equal(Loc.T("crash.showInMods"), vm.ShowLabel);
        Assert.True(vm.CanDisable);
        Assert.True(vm.HasCatalogLink);

        await vm.DisableCommand.ExecuteAsync(null);
        Assert.Equal([mod.Path], disables.Paths);
        Assert.Equal(Loc.T("crash.disabled", "Primitive Survival"), vm.Status);
    }

    // мир закрылся, а игра открыта — выключится после её выхода
    [Fact]
    public async Task ForGame_WorldClosed_GameOpen_DisablesAfterExit()
    {
        var disables = new Disables { GameRunning = true };
        var mod = Print();
        var vm = CrashWindowViewModel.ForGame(Client, Finding(new ModCulprit(mod, mod.ModId, mod.Version, CulpritSource.Message), GameExitKind.WorldClosed),
            disables.Disabler(), new Host());

        Assert.Equal(Loc.T("crash.titleWorld"), vm.Title);
        await vm.DisableCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("crash.disableAfterExit", "Primitive Survival"), vm.Status);
        Assert.Empty(disables.Paths);
        await Assert.Single(disables.Deferred)();
        Assert.Equal([mod.Path], disables.Paths);
    }

    // виновник назван (по modid), но среди модов профиля его нет — выключать нечего, и цель даже не берём
    [Fact]
    public void ForGame_CulpritNotInstalled_NoDisable()
    {
        var disables = new Disables();
        var vm = CrashWindowViewModel.ForGame(Client, Finding(new ModCulprit(null, "expandedfoods", "1.6.2", CulpritSource.GameReport)),
            disables.Disabler(), new Host());

        Assert.False(vm.CanDisable);
        Assert.True(vm.HasCatalogLink);
        Assert.Equal(0, disables.TargetCalls);
        Assert.Equal(Loc.T("crash.culprit", "expandedfoods", "1.6.2"), vm.CulpritText);
        Assert.EndsWith(Loc.T("crash.notInstalled"), vm.HowText);
    }

    [Fact]
    public void ForGame_Show_OpensMyModsWithSearch()
    {
        var host = new Host();
        var mod = Print();
        CrashWindowViewModel.ForGame(Client, Finding(new ModCulprit(mod, mod.ModId, mod.Version, CulpritSource.Stack)), new Disables().Disabler(), host)
            .ShowCommand.Execute(null);
        Assert.Equal([$"switch client-1 {AppTab.Mods}", "search primitivesurvival", "activate"], host.Calls);
    }

    // виновника нет — «Мои моды» без поиска
    [Fact]
    public void ForGame_Show_WithoutCulprit_NoSearch()
    {
        var host = new Host();
        var vm = CrashWindowViewModel.ForGame(Client, Finding(null), new Disables().Disabler(), host);
        Assert.False(vm.CanDisable);
        Assert.False(vm.HasCatalogLink);
        vm.ShowCommand.Execute(null);
        Assert.Equal([$"switch client-1 {AppTab.Mods}", "activate"], host.Calls);
    }

    // ---- окно падения сервера

    private static ServerCrashInfo Crash(string? path = "/srv/vs/Mods/carryon_1.8.0.zip", string? name = "Carry On") => new()
    {
        Id = "c1",
        At = new DateTime(2026, 10, 9, 21, 14, 0),
        Reason = "Too many errors",
        Error = "[carryon] System.InvalidOperationException: Sequence contains no elements",
        Stack = Frames,
        ErrorCount = 37,
        ModId = "carryon",
        ModName = name,
        ModVersion = "1.8.0",
        ModPath = path,
        Source = CulpritSource.Message,
    };

    [Fact]
    public async Task ForServer_DisablesInServerConfig()
    {
        var disables = new Disables();
        var vm = CrashWindowViewModel.ForServer(Server, Crash(), disables.Disabler(), new Host());

        Assert.Equal(Loc.T("crash.titleServer", "Сервер"), vm.Title);
        Assert.Equal(Loc.T("crash.showServer"), vm.ShowLabel);
        Assert.Equal(Loc.T("crash.reason", "Too many errors") + " " + Loc.T("crash.errorCount", 37), vm.ReasonText);
        Assert.True(vm.CanDisable);

        await vm.DisableCommand.ExecuteAsync(null);
        Assert.Equal(["/srv/vs/Mods/carryon_1.8.0.zip"], disables.Paths);
        Assert.Equal(Loc.T("crash.disabledServer", "Carry On"), vm.Status);
    }

    // имени мода агент не знает — в тексте modid
    [Fact]
    public async Task ForServer_WithoutModName_UsesModId()
    {
        var vm = CrashWindowViewModel.ForServer(Server, Crash(name: null), new Disables().Disabler(), new Host());
        await vm.DisableCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("crash.disabledServer", "carryon"), vm.Status);
    }

    // мод не нашёлся среди установленных на сервере (пути нет) — выключать нечего
    [Fact]
    public void ForServer_WithoutPath_NoDisable()
    {
        var disables = new Disables();
        var vm = CrashWindowViewModel.ForServer(Server, Crash(path: null), disables.Disabler(), new Host());
        Assert.False(vm.CanDisable);
        Assert.Equal(0, disables.TargetCalls);
        Assert.EndsWith(Loc.T("crash.notInstalled"), vm.HowText);
    }

    [Fact]
    public void ForServer_Show_OpensServerTab()
    {
        var host = new Host();
        CrashWindowViewModel.ForServer(Server, Crash(), new Disables().Disabler(), host).ShowCommand.Execute(null);
        Assert.Equal([$"switch server-1 {AppTab.Server}", "activate"], host.Calls);
    }
}

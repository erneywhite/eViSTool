using System.Runtime.InteropServices;
using eViSTool.Core.Diagnostics;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

using Row = eViSTool.Presentation.ViewModels.ModErrorRow;

/// <summary>
/// Окно «Ошибки модов»: сводка и строки, какие моды можно выключить, выключение (удачно, после выхода из игры, с
/// ошибкой, несколько строк сразу), страница мода (закрывает окно), игнор и возврат, копирование. Отчёты выдуманные;
/// программа вокруг окна (каталог, отчёт профиля, буфер) и выключатель подменены — диск и игра не трогаются.
/// </summary>
public sealed class ModErrorsViewModelTests
{
    private static readonly GameProfile Client = new() { Id = "client-1", Name = "Игра", Kind = ProfileKind.Client };
    private static readonly GameProfile Server = new() { Id = "server-1", Name = "Сервер", Kind = ProfileKind.Server };
    private static readonly DateTime At = new(2026, 10, 9, 21, 14, 0);

    /// <summary>Программа вокруг окна: что окно у неё просило, по порядку.</summary>
    private sealed class Host : IModErrorsHost
    {
        public List<string> Calls { get; } = [];
        public List<string> Copied { get; } = [];
        public List<GameProfile> Saved { get; } = [];
        /// <summary>Буфер обмена занят: чем он ответит.</summary>
        public Exception? ClipboardFails { get; set; }

        public Task ShowInCatalogAsync(string modId, string? name)
        {
            Calls.Add($"catalog {modId} {name}");
            return Task.CompletedTask;
        }

        public void SaveErrorIgnores(GameProfile profile, ModErrorIgnores ignores)
        {
            Calls.Add("ignores");
            Saved.Add(profile);
        }

        public void SetClipboard(string text)
        {
            if (ClipboardFails is { } ex) throw ex;
            Copied.Add(text);
        }
    }

    /// <summary>Выключатель без игры и диска: что и где выключали; выключение можно придержать или уронить.</summary>
    private sealed class Disables
    {
        public bool GameRunning { get; set; }
        public bool NoTarget { get; set; }
        public int TargetCalls { get; private set; }
        public List<string> Paths { get; } = [];
        public List<Func<Task>> Deferred { get; } = [];
        /// <summary>Чем ответит выключение: null — удачно.</summary>
        public Exception? Fail { get; set; }
        /// <summary>Придержать выключение по пути, пока тест не отпустит.</summary>
        public Dictionary<string, TaskCompletionSource> Gates { get; } = [];

        public ModDisabler Disabler() => new(
            _ => GameRunning, (_, action) => Deferred.Add(action), _ => { },
            p => { TargetCalls++; return NoTarget ? null : new ModTarget(p, null); },
            async (_, path) =>
            {
                if (Gates.TryGetValue(path, out var gate)) await gate.Task;
                if (Fail is { } ex) throw ex;
                Paths.Add(path);
            });
    }

    private static ModErrorLine Line(string id = "carryon", string name = "Carry On", string? version = "1.8.0",
        string? path = @"C:\VintagestoryData\Mods\carryon_1.8.0.zip", int count = 12, string example = "[carryon] boom") =>
        new(id, name, version, path, count, example, CulpritSource.Message);

    /// <summary>Отчёт запуска: моды (по умолчанию — один, Carry On) и ошибки без признаков мода.</summary>
    private static ModErrorReport Report(int unattributed = 0, params ModErrorLine[] mods)
    {
        ModErrorLine[] lines = mods.Length > 0 ? mods : [Line()];
        return new ModErrorReport
        {
            At = At,
            Total = lines.Sum(m => m.Count) + unattributed,
            Unattributed = unattributed,
            Mods = lines,
        };
    }

    private static ModErrorsViewModel Model(ModErrorReport? report = null, GameProfile? profile = null, Disables? disables = null, Host? host = null,
        ModErrorIgnores? ignores = null) =>
        new(profile ?? Client, report ?? Report(), ignores ?? new ModErrorIgnores(), (disables ?? new Disables()).Disabler(), host ?? new Host());

    // ---- сводка и строки

    [Fact]
    public void Summary_ProfileRunTotalsAndMods()
    {
        var report = Report(0, Line(count: 30), Line("hudclock", "HUD Clock", count: 7));
        Assert.Equal(Loc.T("errs.summary", "Игра", Dates.DayMonthClock(At), 37, 2), Model(report).Summary);
    }

    // ошибки без признаков мода — ещё одной фразой
    [Fact]
    public void Summary_WithUnattributed_AddsSentence()
    {
        var report = Report(5, Line(count: 12));
        Assert.Equal(Loc.T("errs.summary", "Игра", Dates.DayMonthClock(At), 17, 1) + " " + Loc.T("errs.unattributed", 5),
            Model(report).Summary);
    }

    [Fact]
    public void Rows_FollowReportOrder_WithCountText()
    {
        var a = Line("primitivesurvival", "Primitive Survival", count: 37);
        var b = Line(count: 12);
        var vm = Model(Report(0, a, b));
        Assert.Equal([a, b], vm.Rows.Select(r => r.Line));
        Assert.Equal(Loc.T("errs.count", 37), vm.Rows[0].CountText);
        Assert.Equal(Loc.T("errs.count", 12), vm.Rows[1].CountText);
        Assert.Equal("", vm.Status);
    }

    // мод не нашёлся среди установленных (пути нет) — выключать нечего, и цель профиля даже не берём
    [Fact]
    public void Row_WithoutPath_CannotDisable()
    {
        var disables = new Disables();
        var vm = Model(Report(0, Line(path: null)), disables: disables);
        var row = Assert.Single(vm.Rows);
        Assert.False(row.CanDisable);
        Assert.False(row.CanRunDisable);
        Assert.False(vm.DisableCommand.CanExecute(row));
        Assert.Equal(0, disables.TargetCalls);
    }

    // удалённый сервер, код которого на этой машине не прочитать, — выключать негде: кнопка недоступна (раньше она была доступна, но ничего не делала)
    [Fact]
    public void Row_WithoutTarget_CannotDisable()
    {
        var vm = Model(Report(0, Line()), Server, new Disables { NoTarget = true });
        Assert.False(Assert.Single(vm.Rows).CanDisable);
        Assert.False(vm.DisableCommand.CanExecute(vm.Rows[0]));
    }

    // цель профиля берётся один раз на строку, когда окно открывается, — не при каждом нажатии
    [Fact]
    public async Task Target_IsTakenWhenWindowOpens()
    {
        var disables = new Disables();
        var vm = Model(Report(0, Line(), Line("hudclock", "HUD Clock", path: @"C:\Mods\hudclock.zip")), disables: disables);
        Assert.Equal(2, disables.TargetCalls);
        await vm.DisableCommand.ExecuteAsync(vm.Rows[0]);
        Assert.Equal(2, disables.TargetCalls);
    }

    [Fact]
    public void Disable_WithoutRow_IsUnavailable()
    {
        Assert.False(Model().DisableCommand.CanExecute(null));
    }

    // ---- выключение

    [Fact]
    public async Task Disable_Client_GameClosed_DisablesNow_AndStaysUnavailable()
    {
        var disables = new Disables();
        var vm = Model(disables: disables);
        var row = vm.Rows[0];
        Assert.True(vm.DisableCommand.CanExecute(row));

        await vm.DisableCommand.ExecuteAsync(row);

        Assert.Equal([@"C:\VintagestoryData\Mods\carryon_1.8.0.zip"], disables.Paths);
        Assert.Equal(Loc.T("crash.disabled", "Carry On"), vm.Status);
        Assert.True(row.IsDone);
        Assert.False(row.IsBusy);
        Assert.False(vm.DisableCommand.CanExecute(row)); // уже выключен — второй раз не нужно
    }

    // игра профиля открыта — выключится после её выхода; кнопка, как и после выключения, недоступна
    [Fact]
    public async Task Disable_Client_GameRunning_DisablesAfterExit()
    {
        var disables = new Disables { GameRunning = true };
        var vm = Model(disables: disables);
        var row = vm.Rows[0];

        await vm.DisableCommand.ExecuteAsync(row);

        Assert.Equal(Loc.T("crash.disableAfterExit", "Carry On"), vm.Status);
        Assert.Empty(disables.Paths);
        Assert.True(row.IsDone);
        Assert.False(vm.DisableCommand.CanExecute(row));

        await Assert.Single(disables.Deferred)(); // игра закрылась
        Assert.Equal([@"C:\VintagestoryData\Mods\carryon_1.8.0.zip"], disables.Paths);
    }

    [Fact]
    public async Task Disable_Server_DisablesInServerConfig()
    {
        var disables = new Disables { GameRunning = true }; // игру программа отслеживает только у клиента
        var vm = Model(Report(0, Line(path: "/srv/vs/Mods/carryon_1.8.0.zip")), Server, disables);

        await vm.DisableCommand.ExecuteAsync(vm.Rows[0]);

        Assert.Equal(["/srv/vs/Mods/carryon_1.8.0.zip"], disables.Paths);
        Assert.Equal(Loc.T("crash.disabledServer", "Carry On"), vm.Status);
    }

    [Fact]
    public async Task Disable_Failure_ShowsText_AndCanRetry()
    {
        var disables = new Disables { Fail = new IOException("Файл занят другим процессом") };
        var vm = Model(disables: disables);
        var row = vm.Rows[0];

        await vm.DisableCommand.ExecuteAsync(row);

        Assert.Equal(Loc.T("crash.disableFailed", "Файл занят другим процессом"), vm.Status);
        Assert.False(row.IsDone);
        Assert.False(row.IsBusy);
        Assert.True(vm.DisableCommand.CanExecute(row)); // можно попробовать ещё раз

        disables.Fail = null;
        await vm.DisableCommand.ExecuteAsync(row);
        Assert.Equal(Loc.T("crash.disabled", "Carry On"), vm.Status);
        Assert.True(row.IsDone);
    }

    // пока мод выключается (агент удалённого сервера отвечает не сразу) — его кнопка недоступна, а соседнюю строку
    // выключать можно
    [Fact]
    public async Task Disable_WhileRunning_RowIsBusy_OtherRowsAvailable()
    {
        var disables = new Disables();
        var gate = new TaskCompletionSource();
        disables.Gates[@"C:\Mods\a.zip"] = gate;
        var vm = Model(Report(0, Line("a", "A", path: @"C:\Mods\a.zip"), Line("b", "B", path: @"C:\Mods\b.zip")), Server, disables);
        var (a, b) = (vm.Rows[0], vm.Rows[1]);
        var changed = new List<string?>();
        a.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        var running = vm.DisableCommand.ExecuteAsync(a);
        Assert.True(a.IsBusy);
        Assert.False(a.CanRunDisable);
        Assert.False(vm.DisableCommand.CanExecute(a));
        Assert.Contains(nameof(Row.CanRunDisable), changed);
        Assert.True(vm.DisableCommand.CanExecute(b));

        await vm.DisableCommand.ExecuteAsync(b);
        Assert.Equal(Loc.T("crash.disabledServer", "B"), vm.Status);
        Assert.True(a.IsBusy);

        gate.SetResult();
        await running;
        Assert.False(a.IsBusy);
        Assert.True(a.IsDone);
        Assert.Equal(Loc.T("crash.disabledServer", "A"), vm.Status);
        Assert.Equal([@"C:\Mods\b.zip", @"C:\Mods\a.zip"], disables.Paths);
    }

    // чего не ждали — не глотаем: уходит в общий обработчик ошибок программы (кнопка остаётся недоступной)
    [Fact]
    public async Task Disable_UnexpectedFailure_IsNotSwallowed()
    {
        var vm = Model(disables: new Disables { Fail = new ArgumentException("ошибка в коде") });
        await Assert.ThrowsAsync<ArgumentException>(() => vm.DisableCommand.ExecuteAsync(vm.Rows[0]));
        Assert.Equal("", vm.Status);
        Assert.False(vm.DisableCommand.CanExecute(vm.Rows[0]));
    }

    // ---- страница мода

    [Fact]
    public void Catalog_OpensModPage_AndClosesWindow()
    {
        var host = new Host();
        var vm = Model(Report(0, Line(), Line("hudclock", "HUD Clock")), host: host);
        var closed = 0;
        vm.CloseRequested += () => closed++;

        vm.CatalogCommand.Execute(vm.Rows[1]);

        Assert.Equal(["catalog hudclock HUD Clock"], host.Calls);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Catalog_WithoutRow_DoesNothing()
    {
        var host = new Host();
        var vm = Model(host: host);
        var closed = 0;
        vm.CloseRequested += () => closed++;
        vm.CatalogCommand.Execute(null);
        Assert.Empty(host.Calls);
        Assert.Equal(0, closed);
    }

    // страница мода есть и у мода, которого нет среди установленных (каталог ищет по modid)
    [Fact]
    public void Catalog_AvailableEvenWithoutPath()
    {
        var vm = Model(Report(0, Line(path: null)));
        Assert.True(vm.CatalogCommand.CanExecute(vm.Rows[0]));
    }

    // ---- «Игнорировать» и «Вернуть»

    // ошибки игры (без признаков мода) — последней строкой: ни выключить, ни страницы мода
    [Fact]
    public void Unattributed_IsLastRow_WithoutDisableOrCatalog()
    {
        var report = Report(5, Line()) with { UnattributedExample = "Exception: boom" };
        var vm = Model(report);
        var game = vm.Rows[^1];
        Assert.Equal(2, vm.Rows.Count);
        Assert.True(game.IsGame);
        Assert.False(game.CanDisable);
        Assert.False(game.HasCatalog);
        Assert.Equal(Loc.T("errs.count", 5), game.CountText);
        Assert.Equal("Exception: boom", game.Line.Example);
    }

    [Fact]
    public void Ignore_MovesRowDown_SavesForProfile_Restore_BringsItBack()
    {
        var host = new Host();
        var ignores = new ModErrorIgnores();
        var report = Report(4, Line("a", "A", count: 30), Line("b", "B", count: 7)) with { UnattributedExample = "game boom" };
        var vm = Model(report, Server, host: host, ignores: ignores);
        var (a, b, game) = (vm.Rows[0], vm.Rows[1], vm.Rows[2]);
        Assert.False(vm.HasIgnored);

        vm.IgnoreCommand.Execute(a);
        vm.IgnoreCommand.Execute(game);

        Assert.Equal([b], vm.Rows);
        Assert.Equal([a, game], vm.Ignored); // порядок отчёта
        Assert.Equal(Loc.T("errs.ignoredHeader", 2), vm.IgnoredHeader);
        Assert.Equal([Server, Server], host.Saved);
        Assert.Equal(7, ignores.Active(report)); // «!» считает только B

        vm.RestoreCommand.Execute(a);
        Assert.Equal([a, b], vm.Rows);
        Assert.Equal([game], vm.Ignored);
        Assert.Equal(37, ignores.Active(report));
    }

    // окно открыли с уже запомненным игнором — строка сразу внизу; всё в игноре — список пуст
    [Fact]
    public void AlreadyIgnored_StartsInIgnoredList()
    {
        var ignores = new ModErrorIgnores();
        var line = Line(example: "[carryon] boom 12");
        ignores.Add("carryon", line.KindList);
        var vm = Model(Report(0, line with { Example = "[carryon] boom 99" }), ignores: ignores); // число другое — вид тот же
        Assert.False(vm.HasRows);
        Assert.Single(vm.Ignored);
    }

    // ---- копирование

    [Fact]
    public void Copy_SummaryAndLinePerMod_AndSaysSo()
    {
        var host = new Host();
        var vm = Model(Report(2, Line(count: 12, example: "[carryon] boom"), Line("hudclock", "HUD Clock", version: null, count: 3, example: "нет текстуры")),
            host: host);

        vm.CopyCommand.Execute(null);

        var nl = Environment.NewLine;
        // версии нет — пробел остаётся
        Assert.Equal([$"{vm.Summary}{nl}Carry On 1.8.0 — 12: [carryon] boom{nl}HUD Clock  — 3: нет текстуры"], host.Copied);
        Assert.Equal(Loc.T("crash.copied"), vm.Status);
    }

    // буфер занят другой программой — молча: ни ошибки, ни «скопировано»
    [Fact]
    public void Copy_ClipboardBusy_IsQuiet()
    {
        foreach (var busy in new Exception[] { new COMException("CLIPBRD_E_CANT_OPEN"), new ExternalException("занят"), new TimeoutException() })
        {
            var vm = Model(host: new Host { ClipboardFails = busy });
            vm.CopyCommand.Execute(null);
            Assert.Equal("", vm.Status);
        }
    }
}

using eViSTool.Core.Diagnostics;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Окно вылета: игра вылетела, виноват установленный мод — можно выключить (dialog.crash.game); сервер упал от ошибок
/// мода (dialog.crash.server); мир закрылся, виновника среди модов профиля нет (dialog.crash.world); виновник не найден
/// (dialog.crash.unknown). Вылеты выдуманные, логи пользователя не читаются; мод-виновник — из модов стенда. Окно
/// только открывается: «Выключить мод» на снимке никто не нажимает.
/// </summary>
internal static class Crash
{
    [SnapshotDialog("crash.game")]
    private static void Game(MainViewModel main)
    {
        var profile = Profile(main);
        var mod = new ModPrint("primitivesurvival", "Primitive Survival", "3.7.6", ModPath(profile, "primitivesurvival_3.7.6.zip"),
            new HashSet<string>(), new HashSet<string>());
        var finding = new CrashFinding(GameExitKind.Crashed, "Game crashed",
            "System.NullReferenceException: Object reference not set to an instance of an object.",
            [
                "PrimitiveSurvival.ModSystem.BEFishBasket.CheckCatch",
                "PrimitiveSurvival.ModSystem.BEFishBasket.OnTick",
                "Vintagestory.Common.GameTickListener.OnTriggered",
                "Vintagestory.Client.NoObf.ClientEventManager.TriggerGameTick",
                "Vintagestory.Client.NoObf.ClientMain.MainGameLoop",
            ],
            new ModCulprit(mod, mod.ModId, mod.Version, CulpritSource.Stack), ErrorCount: 1);
        AppWindows.Current.ShowGameCrash(main, profile, finding);
    }

    [SnapshotDialog("crash.server")]
    private static void Server(MainViewModel main)
    {
        var profile = Profile(main);
        var crash = new ServerCrashInfo
        {
            Id = "snapshot",
            At = new DateTime(2026, 10, 9, 21, 14, 0),
            Reason = "Too many errors",
            Error = "[carryon] System.InvalidOperationException: Sequence contains no elements",
            Stack =
            [
                "System.Linq.ThrowHelper.ThrowNoElementsException",
                "CarryOn.Common.CarrySystem.OnPlayerNowPlaying",
                "Vintagestory.Server.ServerEventManager.TriggerPlayerNowPlaying",
            ],
            ErrorCount = 37,
            ModId = "carryon",
            ModName = "Carry On",
            ModVersion = "1.8.0",
            ModPath = ModPath(profile, "carryon_1.8.0.zip"),
            Source = CulpritSource.Message,
        };
        AppWindows.Current.ShowServerCrash(main, profile, crash);
    }

    [SnapshotDialog("crash.world")]
    private static void World(MainViewModel main)
    {
        // строка перевода мода, которого у профиля уже нет: выключать нечего, страница в каталоге — есть
        var finding = new CrashFinding(GameExitKind.WorldClosed, "Server crashed.",
            "System.FormatException: Input string was not in a correct format.",
            ["System.Text.StringBuilder.AppendFormatHelper", "Vintagestory.API.Config.Lang.Get"],
            new ModCulprit(null, "expandedfoods", "1.6.2", CulpritSource.Translation), ErrorCount: 4);
        AppWindows.Current.ShowGameCrash(main, Profile(main), finding);
    }

    [SnapshotDialog("crash.unknown")]
    private static void Unknown(MainViewModel main)
    {
        var finding = new CrashFinding(GameExitKind.Crashed, "Game crashed",
            "System.AccessViolationException: Attempted to read or write protected memory.",
            ["OpenTK.Graphics.OpenGL.GL.DrawElements", "Vintagestory.Client.NoObf.ClientPlatformWindows.RenderMesh"],
            null, ErrorCount: 1);
        AppWindows.Current.ShowGameCrash(main, Profile(main), finding);
    }

    /// <summary>Профиль стенда («Стенд», папка данных — во временной папке стенда).</summary>
    private static GameProfile Profile(MainViewModel main) =>
        main.ActiveProfile?.Model ?? throw new InvalidOperationException("У стенда нет активного профиля");

    private static string ModPath(GameProfile profile, string file) => Path.Combine(profile.DataDir ?? "", "Mods", file);
}

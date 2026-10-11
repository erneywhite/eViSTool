using eViSTool.Core;
using eViSTool.Core.Game;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Что окну «Уборка диска» (<see cref="CleanupViewModel"/>) нужно от системы: найти, что убрать, убрать в корзину,
/// узнать, запущены ли игра или сервер профиля. По умолчанию всё настоящее; тесты подменяют, чтобы не трогать диск.
/// </summary>
public sealed record CleanupServices
{
    /// <summary>Игра или сервер профиля запущены.</summary>
    public Func<GameProfile, bool> IsRunning { get; init; } = GameProcess.IsRunning;

    /// <summary>Что можно убрать: профиль и его папка данных → виды уборки с пунктами. Идёт в фоне.</summary>
    public Func<GameProfile, string, IReadOnlyList<CleanupGroup>> Scan { get; init; } = ScanDisk;

    /// <summary>Убрать файл или папку в корзину (Windows или freedesktop на Linux). Идёт в фоне.</summary>
    public Action<string> Trash { get; init; } = RecycleBin.Send;

    /// <summary>Папка данных профиля и данные eViSTool для него (версии модов и конфигов, загрузки).</summary>
    private static IReadOnlyList<CleanupGroup> ScanDisk(GameProfile profile, string dataDir)
    {
        var mods = ModUpdateService.ScanLocal(ProfileResolver.Resolve(profile));
        return DiskCleanup.Scan(profile, dataDir, mods, AppPaths.Root, DateTime.UtcNow);
    }
}

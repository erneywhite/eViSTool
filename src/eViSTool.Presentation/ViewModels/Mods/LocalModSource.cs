using System.IO;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;

namespace eViSTool.Presentation.ViewModels.Mods;

/// <summary>
/// Моды своего профиля — с диска этой машины. Папки читаются в фоне; пока вкладка открыта, их раз в две секунды
/// сверяют с отметкой прошлого чтения: моды могли поменять в другом окне (удалённом или втором локальном).
/// </summary>
internal sealed class LocalModSource : IModSource
{
    private readonly ModsViewModel _owner;
    private readonly MainViewModel _main;

    // своя машина: моды могли поменять в другом окне (удалённом или втором локальном) — перечитываем сами
    private readonly IUiTimer _diskWatch = Ui.Timer(TimeSpan.FromSeconds(2));
    private DateTime? _diskStamp;

    /// <summary>Номер чтения папок модов: результат прежнего (профиль сменили, начали новое) отбрасывается.</summary>
    private int _scanGeneration;

    // обычный конструктор, не первичный: подписке на таймер нужно тело, а обработчику — сам источник (ReloadAsync, отметка)
    public LocalModSource(ModsViewModel owner, MainViewModel main)
    {
        _owner = owner;
        _main = main;
        _diskWatch.Tick += (_, _) =>
        {
            if (_owner.IsRemote || _owner.IsBusy || Resolved is not { } profile) return;
            if (ServerMods.StampOf(profile) == _diskStamp) return;
            _ = ReloadAsync();
            _owner.StatusText = Loc.T("mods.changedOutside");
        };
    }

    public ResolvedProfile? Resolved => _main.ActiveProfile?.Resolved;

    /// <summary>Вкладка «Мои моды» открыта — следим за папками модов (закрыта — не тратим на это время).</summary>
    public void SetActive(bool active)
    {
        if (active && !_diskWatch.IsEnabled)
        {
            if (!_owner.IsRemote && Resolved is { } profile && ServerMods.StampOf(profile) != _diskStamp) _ = ReloadAsync();
            _diskWatch.Start();
        }
        else if (!active) _diskWatch.Stop();
    }

    /// <summary>
    /// Папки модов своей машины — в фоне: сотня zip-архивов читается секунду-другую, окно в это время не замирает.
    /// </summary>
    public async Task ReloadAsync()
    {
        if (_main.ActiveProfile is not { } profile) return;
        profile.Refresh(notify: false); // игра могла поменять свои настройки
        var resolved = profile.Resolved;
        var generation = ++_scanGeneration;
        // отметка — до чтения: иначе наблюдатель за папками, не дождавшись, запускал бы его снова и снова
        _diskStamp = ServerMods.StampOf(resolved);
        IReadOnlyList<LocalMod> locals;
        string? error = null;
        try
        {
            locals = await Task.Run(() => ModUpdateService.ScanLocal(resolved));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            locals = [];
            error = ex.Message;
        }
        if (generation != _scanGeneration || _main.ActiveProfile != profile) return;
        _owner.Locals = locals;
        if (error is not null) _owner.StatusText = Loc.T("mods.readFailed", error);
        _owner.Rebuild();
        _owner.NotifyLocalModsChanged();
    }

    public Dictionary<string, int> ConfigCounts(IReadOnlyList<LocalMod> mods)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (Resolved?.Profile is not { } p) return counts;
        var data = string.IsNullOrWhiteSpace(p.DataDir) ? GameInstall.DefaultDataDir : p.DataDir;
        try
        {
            foreach (var g in ModConfigs.List(data, mods).Where(f => f.ModId is not null).GroupBy(f => f.ModId!))
                counts[g.Key] = g.Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // не прочитали папку — просто без ссылки в карточке
        }
        return counts;
    }
}

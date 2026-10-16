using System.Net.Http;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;

namespace eViSTool.Presentation.ViewModels.Mods;

/// <summary>
/// Моды удалённого сервера — со слов его агента: список, папки и версия игры (пути — на той машине). Агент сообщает в
/// статусе, когда моды менялись; отметка не совпала с прочитанной — список поменяли здесь или в другом окне, перечитываем.
/// </summary>
internal sealed class RemoteModSource(ModsViewModel owner, MainViewModel main) : IModSource
{
    private ResolvedProfile? _resolved;
    private DateTime? _stamp;
    private bool _loading;

    /// <summary>
    /// Файлы настроек по модам — запрашиваются у агента после списка модов. При смене профиля не сбрасываются:
    /// до ответа нового сервера карточка видит прежние числа.
    /// </summary>
    private Dictionary<string, int> _configCounts = new(StringComparer.OrdinalIgnoreCase);

    public ResolvedProfile? Resolved => _resolved;

    /// <summary>Сменили профиль: прочитанный у прежнего агента список и его отметка — не про новый.</summary>
    public void Reset()
    {
        _resolved = null;
        _stamp = null;
    }

    public async Task ReloadAsync()
    {
        if (main.Server.RemoteClient is not { } agent) return; // нет связи — список обновится, когда она появится
        var profile = main.ActiveProfile!.Model;
        _loading = true;
        try
        {
            var list = await agent.ModsAsync();
            if (main.ActiveProfile?.Model != profile) return; // пока ждали ответ, переключили профиль
            var first = _resolved is null;
            var (resolved, mods) = ModTargets.Remote(profile, list);
            _resolved = resolved;
            owner.Locals = mods;
            _stamp = list.ChangedAt;
            if (first)
            {
                owner.NotifyProfileRead();
                if (owner.StatusText == Loc.T("mods.remoteWaiting")) owner.StatusText = Loc.T("mods.readFromServer");
            }
            owner.Rebuild();
            owner.NotifyLocalModsChanged();
            if (first && main.AutoCheckUpdates && resolved.GameVersion is not null && owner.CheckCommand.CanExecute(null))
                owner.CheckCommand.Execute(null);
            await CountConfigsAsync(agent, profile);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            owner.StatusText = Loc.T("mods.readFailed", ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Новый статус агента удалённого сервера: моды там поменялись (здесь или в другом окне) — перечитываем.</summary>
    public void OnStatus(AgentStatus status)
    {
        // своё действие ещё не закончилось — его изменение не «чужое», список перечитает оно само
        if (!owner.IsRemote || _loading || owner.IsBusy || owner.ToggleEnabledCommand.IsRunning || owner.DeleteCommand.IsRunning) return;
        if (_resolved is null) _ = ReloadAsync();
        else if (status.ModsChangedAt != _stamp) _ = ReloadChangedAsync();
    }

    private async Task ReloadChangedAsync()
    {
        await ReloadAsync();
        owner.StatusText = Loc.T("mods.changedOutside");
    }

    public Dictionary<string, int> ConfigCounts(IReadOnlyList<LocalMod> mods) => _configCounts;

    /// <summary>Конфиги удалённого сервера — для ссылки «Настройки мода» в карточке. Агент старой версии их не знает — без ссылки.</summary>
    private async Task CountConfigsAsync(AgentClient agent, GameProfile profile)
    {
        try
        {
            var counts = (await agent.ModConfigsAsync()).Where(f => f.ModId is not null)
                .GroupBy(f => f.ModId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            if (main.ActiveProfile?.Model != profile) return;
            _configCounts = counts;
            owner.ShowConfigCounts(counts);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
        }
    }
}

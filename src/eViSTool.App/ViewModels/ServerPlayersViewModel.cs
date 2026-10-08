using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.App.ViewModels;

/// <summary>Игрок в списке вкладки «Игроки»: роль и белый список меняются прямо в строке.</summary>
public sealed partial class PlayerRowViewModel : ObservableObject
{
    private readonly ServerPlayersViewModel _owner;
    private bool _quiet = true;

    public PlayerRowViewModel(ServerPlayersViewModel owner, ServerPlayer player, IReadOnlyList<ServerRole> roles, bool online)
    {
        _owner = owner;
        Player = player;
        // роль, которой нет в serverconfig (удалили) — всё равно показываем, иначе список выбора опустеет
        Roles = player.Role is { } code && roles.All(r => r.Code != code) ? [.. roles, new ServerRole(code, code)] : roles;
        _role = Roles.FirstOrDefault(r => r.Code == player.Role);
        _whitelisted = player.Whitelisted;
        _isOnline = online;
        _quiet = false;
    }

    public ServerPlayer Player { get; }
    public string Name => Player.Name;
    public IReadOnlyList<ServerRole> Roles { get; }

    [ObservableProperty] private ServerRole? _role;
    [ObservableProperty] private bool _whitelisted;
    [ObservableProperty] private bool _isOnline;

    public bool IsBanned => Player.Ban is not null;

    /// <summary>«в игре» или «заходил 07.10.2026 21:00».</summary>
    public string SeenText => IsOnline ? Loc.T("players.online")
        : Player.LastJoin is { } last ? Loc.T("players.lastJoin", last.ToString("dd.MM.yyyy HH:mm")) : "";

    /// <summary>«Бан до 09.10.2026 17:54 · griefing (Console)».</summary>
    public string BanText => Player.Ban is not { } ban ? "" : ServerPlayersViewModel.BanLine(ban);

    /// <summary>Выгнать можно того, кто сейчас в игре, и только командой работающего сервера.</summary>
    public bool CanKick => IsOnline && _owner.CanCommand;

    internal void OnOwnerModeChanged() => OnPropertyChanged(nameof(CanKick));

    partial void OnIsOnlineChanged(bool value)
    {
        OnPropertyChanged(nameof(SeenText));
        OnPropertyChanged(nameof(CanKick));
    }

    partial void OnRoleChanged(ServerRole? oldValue, ServerRole? newValue)
    {
        if (_quiet || newValue is null || newValue.Code == Player.Role) return;
        _ = _owner.ChangeRoleAsync(this, newValue);
    }

    partial void OnWhitelistedChanged(bool value)
    {
        if (_quiet || value == Player.Whitelisted) return;
        _ = _owner.SetWhitelistedAsync(this, value);
    }
}

/// <summary>Запись белого списка или бана в нижних списках вкладки.</summary>
public sealed class PlayerListRowViewModel(PlayerListEntry entry, bool isBan)
{
    public PlayerListEntry Entry { get; } = entry;
    public bool IsBan { get; } = isBan;
    public string Name => Entry.Name ?? Entry.Uid;

    /// <summary>«навсегда · друг (Console)» или «до 09.10.2026 17:54 · …».</summary>
    public string Details { get; } = string.Join("  ·  ", new[]
    {
        entry.IsPermanent || entry.Until is null ? Loc.T("players.forever") : Loc.T("players.until", entry.Until.Value.ToString("dd.MM.yyyy HH:mm")),
        entry.Reason is { Length: > 0 } r && r != "-" ? r : null,
        entry.IssuedBy is { Length: > 0 } by ? Loc.T("players.issuedBy", by) : null,
    }.Where(s => s is not null));
}

/// <summary>
/// Вкладка «Игроки» раздела «Сервер»: кто заходил, роли, белый список, баны. Работающий сервер держит списки в памяти,
/// поэтому всё меняется его командами (они видны в консоли, ответ сервера — в строке состояния вкладки); у остановленного
/// правятся файлы его папки (у удалённого — через агента). Добавить нового игрока по имени можно только командой:
/// UID по имени знает лишь сервер.
/// </summary>
public sealed partial class ServerPlayersViewModel : ObservableObject
{
    private readonly ServerViewModel _server;
    private GameProfile? _profile;
    private bool _active;
    private int _generation;
    private DateTime? _seenChange;          // отметка изменения файлов, по которой список прочитан
    private HashSet<string> _online = new(StringComparer.OrdinalIgnoreCase);
    private string? _awaitReply;            // команда отправлена — первая строка сервера после неё и есть ответ
    private bool _replyStarted;
    private bool _quiet;

    public ServerPlayersViewModel(ServerViewModel server)
    {
        _server = server;
        PlayersView = CollectionViewSource.GetDefaultView(Players);
        PlayersView.Filter = o => o is PlayerRowViewModel p && (Search.Length == 0 || p.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public ObservableCollection<PlayerRowViewModel> Players { get; } = [];
    public ICollectionView PlayersView { get; }
    public ObservableCollection<PlayerListRowViewModel> Whitelist { get; } = [];
    public ObservableCollection<PlayerListRowViewModel> Bans { get; } = [];

    public bool HasPlayers => Players.Count > 0;
    public bool HasWhitelist => Whitelist.Count > 0;
    public bool HasBans => Bans.Count > 0;
    public string WhitelistTitle => Loc.T("players.whitelistTitle", Whitelist.Count);
    public string BansTitle => Loc.T("players.bansTitle", Bans.Count);

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _whitelistOn;
    [ObservableProperty] private string _addName = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _loaded;

    partial void OnSearchChanged(string value) => PlayersView.Refresh();

    /// <summary>Сервер работает под eViSTool: всё меняется командами.</summary>
    public bool ByCommands => _server.Client is not null && _server.State == ServerState.Running;

    /// <summary>Сервер остановлен (и чужого нет): правим файлы — свои напрямую, удалённого через агента.</summary>
    public bool ByFiles => _profile is { } p && !_server.IsServerUp && !_server.HasForeign
                           && _server.State == ServerState.Stopped && (!p.IsRemote || _server.Client is not null);

    public bool CanEdit => Loaded && !IsBusy && (ByCommands || ByFiles);
    public bool CanCommand => Loaded && !IsBusy && ByCommands;

    /// <summary>Как сейчас применяются изменения — строка под заголовком.</summary>
    public string ModeText =>
        ByCommands ? Loc.T("players.modeCommands")
        : ByFiles ? Loc.T("players.modeFiles")
        : _server.HasForeign ? Loc.T("players.modeForeign")
        : _profile is { IsRemote: true } && _server.Client is null ? Loc.T("players.modeOffline")
        : Loc.T("players.modeWait");

    partial void OnIsBusyChanged(bool value) => NotifyMode();
    partial void OnLoadedChanged(bool value) => NotifyMode();

    private bool? _byCommandsShown;

    private void NotifyMode()
    {
        // способ правки сменился (сервер запустили или остановили) — прежний ответ к нему уже не относится
        if (_byCommandsShown is { } was && was != ByCommands) StatusText = "";
        _byCommandsShown = ByCommands;
        OnPropertyChanged(nameof(ByCommands));
        OnPropertyChanged(nameof(ByFiles));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanCommand));
        OnPropertyChanged(nameof(ModeText));
        foreach (var p in Players) p.OnOwnerModeChanged();
    }

    /// <summary>Состояние сервера сменилось (запущен, остановлен, связь) — способ правки другой.</summary>
    public void OnServerStateChanged() => NotifyMode();

    public void OnLanguageChanged()
    {
        NotifyMode();
        if (_active) _ = LoadAsync();
    }

    public void OnProfileSwitched(GameProfile? profile)
    {
        _profile = profile;
        _generation++;
        _seenChange = null;
        _online.Clear();
        _awaitReply = null;
        StatusText = ErrorText = "";
        Players.Clear();
        Whitelist.Clear();
        Bans.Clear();
        Loaded = false;
        NotifyLists();
        if (_active) _ = LoadAsync();
    }

    /// <summary>Вкладку открыли — читаем заново (списки могли поменять в игре или в другом окне).</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (active) _ = LoadAsync();
    }

    /// <summary>Статус агента: кто сейчас в игре и когда менялись файлы игроков (их могли поменять в другом окне).</summary>
    public void ShowStatus(AgentStatus? s)
    {
        var online = s is { State: ServerState.Running } ? s.Players.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        if (!online.SetEquals(_online))
        {
            _online = online;
            foreach (var p in Players) p.IsOnline = _online.Contains(p.Name);
        }
        if (s?.PlayersChangedAt is { } changed && changed != _seenChange && _profile is { IsRemote: true })
        {
            if (_active) _ = LoadAsync();
            else _seenChange = null;
        }
        NotifyMode();
    }

    /// <summary>Сервер на этой машине: файлы поменялись (сервер записал списки, правка в другом окне) — перечитать.</summary>
    public void PollLocal()
    {
        if (!_active || _profile is not { IsRemote: false, DataDir: { } dir } || IsBusy) return;
        if (new ServerPlayers(dir).ChangedAt() != _seenChange) _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var generation = ++_generation;
        try
        {
            ServerPlayersView view;
            DateTime? stamp;
            if (_profile is { IsRemote: true })
            {
                if (_server.Client is not { } client) return; // связи нет — прочитаем, когда появится (ShowStatus)
                stamp = (await client.StatusAsync()).PlayersChangedAt;
                view = await client.PlayersAsync();
            }
            else if (_profile?.DataDir is { } dir)
            {
                (stamp, view) = await Task.Run(() =>
                {
                    var files = new ServerPlayers(dir);
                    var at = files.ChangedAt();
                    return (at, files.Read());
                });
            }
            else return;
            if (generation != _generation) return;
            _seenChange = stamp;
            Show(view);
            Loaded = true;
            if (ErrorText.Length > 0 && !IsBusy) ErrorText = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException
                                       or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            if (generation != _generation) return;
            ErrorText = ex.Message.StartsWith("404") ? Loc.T("players.agentOld")
                : _profile is { IsRemote: true } ? RemoteSecret.Describe(ex) : Loc.T("players.readFailed", ex.Message);
        }
    }

    private void Show(ServerPlayersView view)
    {
        _quiet = true;
        try
        {
            // «как у игры»: с 1.20 выделенный сервер пускает только по белому списку (сам пишет об этом при запуске)
            WhitelistOn = view.WhitelistMode != WhitelistMode.Off;
        }
        finally { _quiet = false; }

        Players.Clear();
        foreach (var p in view.Players) Players.Add(new PlayerRowViewModel(this, p, view.Roles, _online.Contains(p.Name)));
        Whitelist.Clear();
        foreach (var w in view.Whitelist.OrderBy(w => w.Name ?? w.Uid, StringComparer.OrdinalIgnoreCase)) Whitelist.Add(new PlayerListRowViewModel(w, isBan: false));
        Bans.Clear();
        foreach (var b in view.Bans.OrderBy(b => b.Name ?? b.Uid, StringComparer.OrdinalIgnoreCase)) Bans.Add(new PlayerListRowViewModel(b, isBan: true));
        NotifyLists();
    }

    private void NotifyLists()
    {
        OnPropertyChanged(nameof(HasPlayers));
        OnPropertyChanged(nameof(HasWhitelist));
        OnPropertyChanged(nameof(HasBans));
        OnPropertyChanged(nameof(WhitelistTitle));
        OnPropertyChanged(nameof(BansTitle));
    }

    internal static string BanLine(PlayerListEntry ban) => string.Join("  ·  ", new[]
    {
        ban.IsPermanent || ban.Until is null ? Loc.T("players.bannedForever") : Loc.T("players.bannedUntil", ban.Until.Value.ToString("dd.MM.yyyy HH:mm")),
        ban.Reason is { Length: > 0 } r && r != "-" ? r : null,
    }.Where(s => s is not null));

    // ---------- изменения: у работающего — команды, у остановленного — файлы ----------

    /// <summary>
    /// Одно изменение: команда серверу (если работает) или правка файлов (если остановлен). После правки файлов список
    /// перечитывается сразу; после команды — когда сервер запишет файлы (отметка изменения в статусе или времени файлов).
    /// </summary>
    private async Task ApplyAsync(Func<IEnumerable<string>>? commands, PlayerFileEdit? edit)
    {
        IsBusy = true;
        ErrorText = "";
        try
        {
            if (ByCommands && commands is not null && _server.Client is { } client)
            {
                foreach (var command in commands())
                {
                    _awaitReply = command;
                    _replyStarted = false;
                    StatusText = Loc.T("players.sent", command);
                    await client.CommandAsync(command);
                }
                // сервер пишет списки на диск сам; перечитаем чуть позже, даже если отметка не сдвинулась
                _ = ReloadLaterAsync(_generation);
            }
            else if (ByFiles && edit is not null)
            {
                if (_profile is { IsRemote: true }) await _server.Client!.EditPlayersAsync(edit);
                else if (_profile?.DataDir is { } dir) await Task.Run(() => new ServerPlayers(dir).Apply(edit));
                StatusText = Loc.T("players.savedFiles");
                await LoadAsync();
            }
            else
            {
                ErrorText = Loc.T("players.needRunning");
                await LoadAsync(); // вернуть переключатели как было
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or UnauthorizedAccessException
                                       or TaskCanceledException or Newtonsoft.Json.JsonException)
        {
            ErrorText = ex.Message;
            await LoadAsync();
        }
        finally { IsBusy = false; }
    }

    private async Task ReloadLaterAsync(int generation)
    {
        await Task.Delay(2500);
        if (generation == _generation && _active) await LoadAsync();
    }

    /// <summary>Новые строки консоли: первая строка сервера после нашей команды — его ответ, показываем во вкладке.</summary>
    public void OnConsoleLines(IEnumerable<ConsoleLine> lines)
    {
        if (_awaitReply is null) return;
        foreach (var line in lines)
        {
            if (line.Kind == ConsoleLineKind.Input)
            {
                _replyStarted = line.Text == _awaitReply;
                continue;
            }
            if (!_replyStarted || line.Kind == ConsoleLineKind.System) continue;
            var text = ConsoleMarkup.ToPlain(line.Text);
            if (text.Contains("Handling Console Command", StringComparison.Ordinal)) continue; // эхо сервера, ответ — следующая строка
            var cut = text.IndexOf("] ", StringComparison.Ordinal); // «08.10.2026 18:00:00 [Server Notification] …»
            var reply = (cut >= 0 ? text[(cut + 2)..] : text).Trim();
            if (reply.Length == 0 || reply.EndsWith(']')) continue; // пустая строка сервера — ждём следующую
            StatusText = Loc.T("players.reply", reply);
            _awaitReply = null;
            return;
        }
    }

    internal Task ChangeRoleAsync(PlayerRowViewModel row, ServerRole role) =>
        ApplyAsync(() => [PlayerCommands.Role(row.Name, role.Code)], new PlayerFileEdit(row.Player.Uid, Role: role.Code));

    internal Task SetWhitelistedAsync(PlayerRowViewModel row, bool on) =>
        ApplyAsync(() => [on ? PlayerCommands.WhitelistAdd(row.Name) : PlayerCommands.WhitelistRemove(row.Name)],
            new PlayerFileEdit(row.Player.Uid, Whitelisted: on));

    partial void OnWhitelistOnChanged(bool value)
    {
        if (_quiet) return;
        _ = ApplyAsync(() => [PlayerCommands.WhitelistMode(value)], new PlayerFileEdit("", Mode: value ? WhitelistMode.On : WhitelistMode.Off));
    }

    [RelayCommand]
    private Task Ban(PlayerRowViewModel? row) => row is null ? Task.CompletedTask : BanByName(row.Name);

    private Task BanByName(string name)
    {
        var dlg = new BanWindow(name) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return Task.CompletedTask;
        return ApplyAsync(() => [PlayerCommands.Ban(name, dlg.Amount, dlg.Unit, dlg.Reason)], null);
    }

    [RelayCommand]
    private Task Unban(PlayerRowViewModel? row) => row is null ? Task.CompletedTask
        : ApplyAsync(() => [PlayerCommands.Unban(row.Name)], new PlayerFileEdit(row.Player.Uid, Unban: true));

    [RelayCommand]
    private Task Kick(PlayerRowViewModel? row)
    {
        if (row is null) return Task.CompletedTask;
        if (MessageBox.Show(Application.Current.MainWindow, Loc.T("players.kickConfirm", row.Name), "eViSTool",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return Task.CompletedTask;
        return ApplyAsync(() => [PlayerCommands.Kick(row.Name)], null);
    }

    /// <summary>Разрешить один раз сменить класс и облик; игроку в игре — сообщение в чат, как это сделать.</summary>
    [RelayCommand]
    private Task AllowCharSel(PlayerRowViewModel? row) => row is null ? Task.CompletedTask
        : ApplyAsync(() => row.IsOnline
            ? [PlayerCommands.AllowCharSelOnce(row.Name), PlayerCommands.TellNear(row.Name, Loc.T("players.charselMessage"))]
            : [PlayerCommands.AllowCharSelOnce(row.Name)], null);

    /// <summary>Нижние списки: убрать из белого списка или снять бан (в том числе с тех, кто ни разу не заходил).</summary>
    [RelayCommand]
    private Task RemoveEntry(PlayerListRowViewModel? row) => row is null ? Task.CompletedTask
        : row.IsBan
            ? ApplyAsync(() => [PlayerCommands.Unban(row.Name)], new PlayerFileEdit(row.Entry.Uid, Unban: true))
            : ApplyAsync(() => [PlayerCommands.WhitelistRemove(row.Name)], new PlayerFileEdit(row.Entry.Uid, Whitelisted: false));

    private string? CheckedName()
    {
        var name = AddName.Trim();
        if (PlayerCommands.IsValidName(name)) return name;
        ErrorText = Loc.T("players.badName");
        return null;
    }

    [RelayCommand]
    private async Task AddToWhitelist()
    {
        if (CheckedName() is not { } name) return;
        await ApplyAsync(() => [PlayerCommands.WhitelistAdd(name)], null);
        if (ErrorText.Length == 0) AddName = "";
    }

    [RelayCommand]
    private async Task BanName()
    {
        if (CheckedName() is not { } name) return;
        await BanByName(name);
        if (ErrorText.Length == 0) AddName = "";
    }
}

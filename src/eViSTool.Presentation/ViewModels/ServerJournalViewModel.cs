using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Строка журнала: когда (по часам окна), кто и откуда, что сделано и что именно поменялось. Warn — неудачная попытка
/// подключиться. Подробностей много — видны первые <see cref="Folded"/>, остальные раскрываются кнопкой.
/// </summary>
public sealed partial class JournalRow(string timeText, string whoText, string actionText, bool warn, IReadOnlyList<string> details)
    : ObservableObject
{
    public const int Folded = 5;

    public string TimeText { get; } = timeText;
    public string WhoText { get; } = whoText;
    public string ActionText { get; } = actionText;
    public bool Warn { get; } = warn;
    public IReadOnlyList<string> Details { get; } = details;

    public bool HasDetails => Details.Count > 0;
    public bool CanExpand => Details.Count > Folded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownDetails), nameof(ExpandText))]
    private bool _isExpanded;

    /// <summary>Что видно сейчас: всё или первые <see cref="Folded"/>.</summary>
    public IReadOnlyList<string> ShownDetails => IsExpanded || !CanExpand ? Details : [.. Details.Take(Folded)];

    /// <summary>«и ещё 7» — свёрнуто, «свернуть» — раскрыто.</summary>
    public string ExpandText => IsExpanded ? Loc.T("journal.detailsLess") : Loc.T("journal.detailsMore", Details.Count - Folded);

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

/// <summary>
/// Вкладка «Журнал»: кто и что делал с сервером через агента за 30 дней — удалённо и с этого компьютера, и неудачные
/// попытки подключиться. Пишет агент (<see cref="ServerJournal"/>); вкладка перечитывает журнал, когда он пополнился.
/// </summary>
public sealed partial class ServerJournalViewModel : ObservableObject
{
    private readonly ServerViewModel _server;
    private readonly IUiTimer _timer = Ui.Timer(TimeSpan.FromSeconds(30));
    private GameProfile? _profile;
    private bool _active;
    private int _generation;
    private DateTime? _seenChange;

    [ObservableProperty] private bool _loaded;
    [ObservableProperty] private string _statusText = "";

    /// <summary>Сколько строк показывать: за 30 дней на оживлённом сервере их тысячи, а смотрят обычно последние.</summary>
    public const int MaxRows = 500;

    public ObservableCollection<JournalRow> Rows { get; } = [];
    public bool HasRows => Rows.Count > 0;
    public bool IsEmpty => Loaded && Rows.Count == 0 && StatusText.Length == 0;

    /// <summary>«Показаны последние 500 из 1 234» — когда записей больше, чем строк.</summary>
    [ObservableProperty] private string _moreText = "";

    public ServerJournalViewModel(ServerViewModel server)
    {
        _server = server;
        // агент на этой машине мог уйти после простоя — тогда журнал читается с диска, и отметки в статусе нет
        _timer.Tick += (_, _) => _ = LoadAsync(_generation);
    }

    private IServerData? Data => _server.Schedule.Data;

    public void OnProfileSwitched(GameProfile? profile)
    {
        if (profile?.Id == _profile?.Id && profile?.IsRemote == _profile?.IsRemote) return;
        _profile = profile;
        _generation++;
        _seenChange = null;
        Loaded = false;
        StatusText = "";
        Rows.Clear();
        MoreText = "";
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasRows));
        if (_active) _ = LoadAsync(_generation);
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (active)
        {
            _ = LoadAsync(_generation);
            _timer.Start();
        }
        else _timer.Stop();
    }

    /// <summary>Журнал пополнился (отметка в статусе агента) или связь появилась — перечитать открытую вкладку.</summary>
    public void ShowStatus(AgentStatus? status)
    {
        if (status is null || !_active) return;
        if (!Loaded || status.JournalChangedAt != _seenChange)
        {
            _seenChange = status.JournalChangedAt;
            _ = LoadAsync(_generation);
        }
    }

    private async Task LoadAsync(int generation)
    {
        if (Data is not { } data) return;
        try
        {
            var entries = await data.LoadJournalAsync();
            if (generation != _generation) return;
            Rows.Clear();
            foreach (var e in entries.Take(MaxRows)) Rows.Add(Row(e));
            MoreText = entries.Count > MaxRows ? Loc.T("journal.more", MaxRows, entries.Count) : "";
            StatusText = "";
            Loaded = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException
                                       or TaskCanceledException or Newtonsoft.Json.JsonException)
        {
            if (generation != _generation) return;
            StatusText = data.IsRemote && AgentHttpException.IsOldAgent(ex)
                ? Loc.T("journal.oldAgent")
                : Loc.T("journal.failed", data.IsRemote ? RemoteSecret.Describe(ex) : ex.Message);
        }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasRows));
    }

    public static JournalRow Row(JournalEntry e)
    {
        var when = e.At.Kind == DateTimeKind.Unspecified ? e.At : e.At.ToLocalTime();
        var name = string.IsNullOrWhiteSpace(e.Who) ? "?" : e.Who;
        var who = e.Remote ? $"{name} · {e.Ip ?? "?"}" : Loc.T("journal.local", name);
        return new JournalRow(Dates.DayMonthClock(when), who, ActionText(e), e.Action is "authFailed" or "authBlocked",
            [.. (e.Details ?? []).Select(DetailText)]);
    }

    /// <summary>Подробность на языке окна; неизвестный код (агент новее окна) — как есть.</summary>
    public static string DetailText(JournalDetail d)
    {
        string A(int i) => i < d.Args.Count ? d.Args[i] : "";
        return d.Kind switch
        {
            "field" => Loc.T("journal.d.field", A(0), A(1), A(2)),
            "fieldAdded" => Loc.T("journal.d.fieldAdded", A(0), A(1)),
            "fieldRemoved" => Loc.T("journal.d.fieldRemoved", A(0), A(1)),
            "secret" => Loc.T("journal.d.secret", A(0)),
            "playerRole" => Loc.T("journal.d.playerRole", A(0), A(1).Length > 0 ? A(1) : "—", A(2)),
            "playerWhitelist" => A(1) == "on" ? Loc.T("journal.d.whitelistOn", A(0)) : Loc.T("journal.d.whitelistOff", A(0)),
            "playerUnban" => Loc.T("journal.d.unban", A(0)),
            "whitelistMode" => Loc.T("journal.d.whitelistMode", ModeText(A(0)), ModeText(A(1))),
            "modVersion" => A(2).Length > 0 ? Loc.T("journal.d.modUpdated", A(0), A(1), A(2)) : Loc.T("journal.d.modNew", A(0), A(1)),
            _ => string.Join(" ", d.Args.Prepend(d.Kind)),
        };
    }

    private static string ModeText(string mode) => mode switch
    {
        "Off" => Loc.T("journal.mode.off"),
        "On" => Loc.T("journal.mode.on"),
        _ => Loc.T("journal.mode.default"),
    };

    /// <summary>Подпись действия на языке окна; неизвестный код (агент новее окна) — как есть, с подробностями.</summary>
    public static string ActionText(JournalEntry e)
    {
        var a = e.Args.Count > 0 ? e.Args[0] : "";
        return e.Action switch
        {
            "start" => Loc.T("journal.act.start"),
            "stop" => Loc.T("journal.act.stop"),
            "restart" => Loc.T("journal.act.restart"),
            "kill" => Loc.T("journal.act.kill"),
            "command" => Loc.T("journal.act.command", a),
            "backup" => Loc.T("journal.act.backup"),
            "restore" => Loc.T("journal.act.restore", a),
            "backupDelete" => Loc.T("journal.act.backupDelete", a),
            "schedule" => Loc.T("journal.act.schedule"),
            "notify" => Loc.T("journal.act.notify"),
            "announcements" => Loc.T("journal.act.announcements"),
            "modInstall" => Loc.T("journal.act.modInstall", a),
            "modOn" => Loc.T("journal.act.modOn", a),
            "modOff" => Loc.T("journal.act.modOff", a),
            "modDelete" => Loc.T("journal.act.modDelete", a),
            "players" => Loc.T("journal.act.players"),
            "modConfigSave" => Loc.T("journal.act.modConfigSave", a),
            "modConfigUndo" => Loc.T("journal.act.modConfigUndo", a),
            "modConfigReset" => Loc.T("journal.act.modConfigReset", a),
            "config" => Loc.T("journal.act.config"),
            "configGenerate" => Loc.T("journal.act.configGenerate"),
            "statsOn" => Loc.T("journal.act.statsOn"),
            "statsOff" => Loc.T("journal.act.statsOff"),
            "statsClear" => Loc.T("journal.act.statsClear"),
            "selfUpdate" => Loc.T("journal.act.selfUpdate", a),
            "authFailed" => Loc.T("journal.act.authFailed"),
            "authBlocked" => Loc.T("journal.act.authBlocked"),
            _ => string.Join(" ", e.Args.Prepend(e.Action)),
        };
    }
}

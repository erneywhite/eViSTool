using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Versioning;

namespace eViSTool.App.ViewModels;

/// <summary>
/// Вкладка «История» в «Моих модах»: сеансы изменений модов активного профиля (новые сверху) и детали выбранного
/// для правой панели. Свой профиль читается из файла, удалённый сервер — у его агента.
/// </summary>
public sealed partial class ModHistoryViewModel : ObservableObject
{
    public ObservableCollection<HistorySessionRow> Sessions { get; } = [];

    [ObservableProperty] private HistorySessionRow? _selected;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isLoading;

    private IReadOnlyList<HistorySessionRow> _all = [];
    private string _search = "";
    private int _load;

    public bool HasSelected => Selected is not null;
    partial void OnSelectedChanged(HistorySessionRow? value) => OnPropertyChanged(nameof(HasSelected));

    /// <summary>Прочитать историю цели. <paramref name="installed"/> — что стоит сейчас (для пометки «сейчас 1.7.3»).</summary>
    public async Task LoadAsync(ModTarget target, IReadOnlyList<LocalMod> installed, CancellationToken ct = default)
    {
        var load = ++_load;
        IsLoading = true;
        Status = Loc.T("hist.loading");
        try
        {
            var entries = await ModTargets.HistoryAsync(target, ct);
            if (load != _load) return;
            var now = installed.Where(m => m.Info is not null)
                .GroupBy(m => m.Info!.ModId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Info!.Version, StringComparer.OrdinalIgnoreCase);
            var server = target.Profile.Kind == ProfileKind.Server;
            _all = [.. ModHistory.Kept(ModHistory.Sessions(entries)).Select(s => new HistorySessionRow(s, now, server))];
            Show();
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or TaskCanceledException
                                       or UnauthorizedAccessException)
        {
            if (load != _load) return;
            _all = [];
            Show();
            // агент сервера старее 0.10.2 истории не знает
            Status = ex.Message.StartsWith("404") ? Loc.T("hist.agentOld") : Loc.T("hist.loadFailed", ex.Message);
        }
        finally
        {
            if (load == _load) IsLoading = false;
        }
    }

    /// <summary>Поиск «Моих модов» действует и здесь: остаются сеансы, где менялся подходящий мод.</summary>
    public void SetSearch(string search)
    {
        _search = search.Trim();
        Show();
    }

    private void Show()
    {
        var keep = Selected?.Session.Id;
        Sessions.Clear();
        foreach (var row in _all.Where(r => r.Matches(_search))) Sessions.Add(row);
        Selected = Sessions.FirstOrDefault(r => r.Session.Id == keep) ?? Sessions.FirstOrDefault();
        Status = _all.Count == 0 ? Loc.T("hist.empty") : Sessions.Count == 0 ? Loc.T("hist.nothingFound") : "";
    }
}

/// <summary>Строка истории — один сеанс: заголовок, когда, откуда, итог по модам.</summary>
public sealed class HistorySessionRow
{
    public ModHistorySession Session { get; }
    public IReadOnlyList<HistoryChangeRow> Changes { get; }

    public HistorySessionRow(ModHistorySession session, IReadOnlyDictionary<string, string> installedNow, bool server)
    {
        Session = session;
        Changes = [.. session.Net.Select(c => new HistoryChangeRow(c, installedNow))];
        Title = TitleFor(Changes);
        var start = session.Start.ToLocalTime();
        When = Day(start) + (session.End - session.Start >= TimeSpan.FromMinutes(1) ? "–" + session.End.ToLocalTime().ToString("HH:mm", Loc.Culture) : "");
        Closed = session.LaunchedAt is { } launched
            ? Loc.T(server ? "hist.beforeServer" : "hist.beforeGame", launched.ToLocalTime().ToString("HH:mm", Loc.Culture))
            : Loc.T("hist.notLaunched");
        Sources = string.Join(", ", session.Sources.Select(s => SourceText(s)));
        Summary = Changes.Count == 0 ? Loc.T("hist.noNet") : string.Join(" · ", Changes.Select(c => $"{c.Title} {c.Versions}"));
        Steps = [.. session.Changes.GroupBy(c => c.Op).Select(g =>
            $"{g.First().At.ToLocalTime().ToString("HH:mm", Loc.Culture)}  {SourceText(g.First().Source)}: "
            + string.Join(", ", g.Select(c => $"{c.Title} {HistoryChangeRow.Arrow(c.From, c.To)}")))];
    }

    public string Title { get; }
    public string When { get; }

    /// <summary>«до запуска игры в 09:35» или «после этого игру ещё не запускали».</summary>
    public string Closed { get; }
    public string WhenLine => $"{When} · {Closed}";
    public string Sources { get; }
    public string Summary { get; }

    /// <summary>Нажатия сеанса по порядку: «09:14  «Обновить всё»: Footprints 1.2.12 → 1.2.13, …».</summary>
    public IReadOnlyList<string> Steps { get; }

    public bool Matches(string search) => search.Length == 0
        || Session.Changes.Any(c => c.ModId.Contains(search, StringComparison.OrdinalIgnoreCase)
                                    || (c.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));

    /// <summary>«Обновлено 5 модов, установлен 1» — по итогу сеанса.</summary>
    private static string TitleFor(IReadOnlyList<HistoryChangeRow> changes)
    {
        if (changes.Count == 0) return Loc.T("hist.titleNone");
        var parts = new List<string>();
        void Add(string key, int n)
        {
            if (n > 0) parts.Add(Loc.Plural(key, n));
        }
        Add("hist.updated", changes.Count(c => c.Kind == ChangeKind.Updated));
        Add("hist.rolledBack", changes.Count(c => c.Kind == ChangeKind.Downgraded));
        Add("hist.installed", changes.Count(c => c.Kind == ChangeKind.Installed));
        Add("hist.removed", changes.Count(c => c.Kind == ChangeKind.Removed));
        var text = string.Join(", ", parts.Select((p, i) => i == 0 ? p : char.ToLower(p[0], Loc.Culture) + p[1..]));
        return text;
    }

    /// <summary>Подпись источника; ключи — целиком, чтобы проверка словарей их видела.</summary>
    public static string SourceText(string source) => source switch
    {
        ModHistorySource.Update => Loc.T("hist.src.update"),
        ModHistorySource.UpdateAll => Loc.T("hist.src.updateAll"),
        ModHistorySource.Catalog => Loc.T("hist.src.catalog"),
        ModHistorySource.Zip => Loc.T("hist.src.zip"),
        ModHistorySource.Pack => Loc.T("hist.src.pack"),
        ModHistorySource.Dependencies => Loc.T("hist.src.deps"),
        ModHistorySource.Schedule => Loc.T("hist.src.schedule"),
        ModHistorySource.Rollback => Loc.T("hist.src.rollback"),
        _ => Loc.T("hist.src.manual"),
    };

    private static string Day(DateTime t)
    {
        var time = t.ToString("HH:mm", Loc.Culture);
        if (t.Date == DateTime.Today) return Loc.T("stats.today", time);
        if (t.Date == DateTime.Today.AddDays(-1)) return Loc.T("stats.yesterday", time);
        return t.ToString("ddd dd.MM, ", Loc.Culture) + time;
    }
}

public enum ChangeKind { Updated, Downgraded, Installed, Removed }

/// <summary>Мод в сеансе: версия до и после, и что с ним сейчас, если с тех пор его меняли.</summary>
public sealed class HistoryChangeRow(ModHistoryEntry change, IReadOnlyDictionary<string, string> installedNow)
{
    public ModHistoryEntry Change { get; } = change;
    public string Title => Change.Title;
    public string ModId => Change.ModId;
    public string Versions => Arrow(Change.From, Change.To);

    public ChangeKind Kind => Change.From is null ? ChangeKind.Installed
        : Change.To is null ? ChangeKind.Removed
        : ModVersion.ParseOrNull(Change.To)?.CompareTo(ModVersion.ParseOrNull(Change.From)) < 0 ? ChangeKind.Downgraded
        : ChangeKind.Updated;

    /// <summary>С тех пор мод меняли: «сейчас 1.7.3» / «сейчас не установлен». Пусто — стоит то, что стало после сеанса.</summary>
    public string Later
    {
        get
        {
            installedNow.TryGetValue(Change.ModId, out var now);
            if (string.Equals(now, Change.To, StringComparison.OrdinalIgnoreCase)) return "";
            return now is null ? Loc.T("hist.nowAbsent") : Loc.T("hist.nowVersion", now);
        }
    }

    public bool HasLater => Later.Length > 0;

    /// <summary>«1.2.12 → 1.2.13», «установлен 1.0.0», «2.0.1 → удалён».</summary>
    public static string Arrow(string? from, string? to) =>
        from is null ? Loc.T("hist.arrowInstalled", to) : to is null ? Loc.T("hist.arrowRemoved", from) : $"{from} → {to}";
}

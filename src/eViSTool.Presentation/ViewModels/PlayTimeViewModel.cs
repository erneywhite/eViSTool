using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Строка «по серверам»: имя (из избранного игры, своих серверов или адрес) и время.</summary>
public sealed record PlayServerRow(string Name, string Time);

/// <summary>
/// «Время в игре» в сводке «Моих модов» (только у клиентских профилей): всего в профиле, последняя сессия, по
/// серверам, во всех профилях. Сессии — из логов игры (<see cref="PlayLedger"/>); логи читаются в фоне, когда профиль
/// открывают и пока идёт игра (вместе с отчётом об ошибках).
/// </summary>
public sealed partial class PlayTimeViewModel : ObservableObject
{
    private int _generation;

    /// <summary>Профиль клиентский — секция видна.</summary>
    [ObservableProperty] private bool _isVisible;
    /// <summary>Сессии есть — видны цифры; нет — строка «пока нет».</summary>
    [ObservableProperty] private bool _hasSessions;
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _lastText = "";
    [ObservableProperty] private IReadOnlyList<PlayServerRow> _servers = [];
    /// <summary>Клиентских профилей больше одного — строка «во всех профилях».</summary>
    [ObservableProperty] private bool _hasAllProfiles;
    [ObservableProperty] private string _allProfilesText = "";
    [ObservableProperty] private string _sinceText = "";

    /// <summary>Проходы по логам — по одному: два сразу читали и писали один учёт, и один затирал другой.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Перечитать логи (в фоне) и показать; ответ на устаревший запрос (профиль сменили) — отбрасывается.
    /// <paramref name="allProfiles"/> — читать логи и остальных клиентских профилей (при открытии профиля); пока идёт
    /// игра (раз в полминуты) — только активного, учёт остальных берётся с диска.
    /// </summary>
    public async Task RefreshAsync(GameProfile? profile, IReadOnlyList<GameProfile> all, bool allProfiles = true)
    {
        var generation = ++_generation;
        if (profile is not { Kind: ProfileKind.Client })
        {
            IsVisible = false;
            return;
        }
        var clients = all.Where(p => p.Kind == ProfileKind.Client).ToList();
        if (!clients.Any(p => p.Id == profile.Id)) clients.Add(profile);

        await _gate.WaitAsync();
        try
        {
            if (generation != _generation) return; // пока ждали очереди, пришёл запрос новее — он и прочитает
            var (ledgers, names) = await Task.Run(() =>
                (clients.ToDictionary(p => p.Id, p => allProfiles || p.Id == profile.Id ? Scan(p) : PlayLedger.Load(p.Id)),
                 Names(profile, all)));
            if (generation != _generation) return;
            // учёт активного профиля сейчас не прочитать — оставляем то, что уже показано
            if (ledgers[profile.Id] is not { } own) return;
            Show(profile, own, [.. ledgers.Values.OfType<PlayLedger>()], names);
            IsVisible = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Учёт профиля с новыми сессиями из его логов; null — учёт сейчас не прочитать (тогда и не пишем поверх).</summary>
    private static PlayLedger? Scan(GameProfile p)
    {
        if (PlayLedger.Load(p.Id) is not { } ledger) return null;
        try
        {
            if (ledger.Scan(DataDirOf(p))) ledger.Save(p.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // нет места или прав на запись — покажем прочитанное, сохраним в следующий раз
        }
        return ledger;
    }

    private static string DataDirOf(GameProfile p) => string.IsNullOrWhiteSpace(p.DataDir) ? GameInstall.DefaultDataDir : p.DataDir;

    /// <summary>Адрес → имя: избранное игры профиля и свои серверы (профили eViSTool).</summary>
    private static Dictionary<string, string> Names(GameProfile profile, IReadOnlyList<GameProfile> all)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in PlayTargets.Favorites(DataDirOf(profile))) names.TryAdd(PlaySessions.Address(t.Address), t.Name);
        foreach (var s in all)
            if (PlayTargets.Own(s) is { } own)
                names[PlaySessions.Address(own.Address)] = own.Name; // свой сервер — его имя в программе
        return names;
    }

    internal void Show(GameProfile profile, PlayLedger ledger, IReadOnlyList<PlayLedger> all, IReadOnlyDictionary<string, string> names)
    {
        HasSessions = ledger.All.Count > 0;
        TotalText = Duration(ledger.Total);
        LastText = ledger.Last is { } last ? $"{Dates.Relative(last.StartUtc.ToLocalTime())} · {Duration(last.Length)}" : "";
        Servers = [.. ledger.ByServer().Select(s => new PlayServerRow(Title(s.Server, names), Duration(s.Time)))];
        // одну и ту же сессию (профили с общей папкой данных) не считаем дважды
        var everything = all.SelectMany(l => l.All).DistinctBy(s => s.StartUtc).Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Length);
        HasAllProfiles = all.Count > 1 && everything > ledger.Total;
        AllProfilesText = Duration(everything);
        SinceText = ledger.SinceUtc is { } since ? Loc.T("play.since", Dates.Day(since.ToLocalTime())) : "";
    }

    /// <summary>Подпись строки: «Одиночная игра · мир», имя сервера из избранного или своих, иначе адрес.</summary>
    private static string Title(string? server, IReadOnlyDictionary<string, string> names) =>
        server is null ? Loc.T("play.single")
        : server.StartsWith(PlaySessions.WorldPrefix, StringComparison.Ordinal) ? Loc.T("play.world", server[PlaySessions.WorldPrefix.Length..])
        : names.TryGetValue(server, out var name) ? name : server;

    /// <summary>«12 ч 05 мин», «45 мин» — часы не сворачиваются в дни: так нагляднее, сколько наиграно.</summary>
    public static string Duration(TimeSpan t) => ServerStatsViewModel.Duration(t);
}

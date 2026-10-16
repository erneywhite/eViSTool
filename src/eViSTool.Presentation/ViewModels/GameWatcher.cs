using System.IO;
using eViSTool.Core.Diagnostics;
using eViSTool.Core.Game;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Наблюдатель за игрой главного окна. Раз в пару секунд проверяет, запущена ли игра активного клиентского профиля, а
/// за запущенной следит: её логи — с момента запуска; вылет или закрытие мира — окно с виновником; «ошибки модов» —
/// раз в полминуты и после выхода. Сессия привязана к профилю, где запущена игра, — переключение активного профиля её
/// не обрывает. Что сделать, когда игра закроется, тоже копится здесь.
/// </summary>
/// <param name="activeClient">Активный профиль, если он клиентский; иначе null.</param>
/// <param name="activeChecked">Проверили профиль из <paramref name="activeClient"/>: запущена ли его игра. Пока
/// проверяли, активный профиль могли сменить, — сверяет главная модель.</param>
/// <param name="errorReport">Свежий отчёт «ошибок модов» запуска игры профиля.</param>
/// <param name="pendingDone">Игра профиля закрылась, и отложенные до её выхода дела выполнены (было хотя бы одно).</param>
/// <param name="exited">Игра закрылась и наблюдение окончено (окно о новой версии ждало этого).</param>
/// <param name="crashed">Вылет или закрытие мира с ошибкой.</param>
public sealed class GameWatcher(
    Func<GameProfile?> activeClient,
    Action<GameProfile?, bool> activeChecked,
    Action<GameProfile, ModErrorReport> errorReport,
    Action<GameProfile> pendingDone,
    Action exited,
    Action<GameProfile, CrashFinding> crashed)
{
    // раз в пару секунд: запущена ли игра активного клиентского профиля
    private readonly IUiTimer _timer = Ui.Timer(TimeSpan.FromSeconds(2));
    private bool _subscribed;
    private bool _checking;

    private GameSession? _session;
    private GameProfile? _sessionProfile;
    private readonly List<(string ProfileId, Func<Task> Action)> _afterExit = [];
    private int _errorTicks;

    /// <summary>
    /// Включить опрос по таймеру. Повторный вызов только запускает таймер: на Tick подписываемся один раз,
    /// иначе проверки шли бы по две за тик.
    /// </summary>
    public void Start()
    {
        if (!_subscribed)
        {
            _timer.Tick += (_, _) => _ = CheckAsync();
            _subscribed = true;
        }
        _timer.Start();
    }

    /// <summary>Идёт наблюдение за запущенной игрой (какого угодно профиля).</summary>
    public bool IsWatching => _sessionProfile is not null;

    /// <summary>Игра этого профиля сейчас запущена (по последней проверке).</summary>
    public bool IsRunningFor(GameProfile profile) => _sessionProfile?.Id == profile.Id;

    /// <summary>
    /// Сделать, когда игра профиля закроется: игра при выходе перезаписывает свои настройки (и список выключенных
    /// модов) — менять их, пока она открыта, бесполезно.
    /// </summary>
    public void RunAfterExit(GameProfile profile, Func<Task> action) => _afterExit.Add((profile.Id, action));

    private async Task RunPendingAsync(GameProfile profile)
    {
        var due = _afterExit.Where(a => a.ProfileId == profile.Id).ToList();
        _afterExit.RemoveAll(a => a.ProfileId == profile.Id);
        foreach (var (_, action) in due)
        {
            try { await action(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { }
        }
        if (due.Count > 0) pendingDone(profile);
    }

    /// <summary>Одна проверка; пока предыдущая не закончилась, новая не начинается.</summary>
    public async Task CheckAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            var active = activeClient();
            var activePids = active is null ? [] : await Task.Run(() => GameProcess.FindClientPids(active));
            activeChecked(active, activePids.Count > 0);

            if (_session is { } session && _sessionProfile is { } watched)
            {
                var alive = watched == active ? activePids.Count > 0 : await Task.Run(() => GameProcess.FindClientPids(watched).Count > 0);
                var finding = await Task.Run(() => alive ? session.Poll(() => Fingerprints(watched)) : session.Finish(() => Fingerprints(watched)));
                // «ошибки модов» — раз в полминуты, пока игра идёт, и после выхода
                if (!alive || ++_errorTicks % 15 == 0)
                    errorReport(watched, await Task.Run(() => session.ErrorReport(() => Fingerprints(watched))));
                if (!alive)
                {
                    (_session, _sessionProfile) = (null, null);
                    await RunPendingAsync(watched); // отложенное до выхода из игры (например, выключение мода из окна вылета)
                    exited();
                }
                if (finding is not null) crashed(watched, finding);
            }
            else if (active is not null && activePids.Count > 0)
            {
                // игра этого профиля только что запущена (нами или как угодно) — начинаем следить с её старта
                var started = DateTime.UtcNow;
                try { using var proc = System.Diagnostics.Process.GetProcessById(activePids[0]); started = proc.StartTime.ToUniversalTime(); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                var dataDir = string.IsNullOrWhiteSpace(active.DataDir) ? GameInstall.DefaultDataDir : active.DataDir;
                _session = new GameSession(dataDir, started, active.GameDir);
                _sessionProfile = active;
                // запуск игры закрывает строку истории модов профиля: всё, что поменяли до него, — одним сеансом
                ModHistory.MarkLaunch(ModHistory.FileFor(active.Id), started);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // логи не прочитать — следующая проверка через пару секунд
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Отпечатки модов профиля — по чему узнавать их в логах.</summary>
    private static ModFingerprints Fingerprints(GameProfile profile) =>
        ModFingerprints.Build(ModUpdateService.ScanLocal(ProfileResolver.Resolve(profile)));
}

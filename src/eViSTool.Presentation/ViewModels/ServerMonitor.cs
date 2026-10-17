using System.Net.Http;
using eViSTool.Core.Game;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Хозяин наблюдателя (<see cref="ServerMonitor"/>) — раздел «Сервер». У него наблюдатель узнаёт профиль и состояние,
/// ему сообщает, что увидел. Всё вызывается в потоке окна, в порядке прохода цикла; исключение из вызова цикл разбирает
/// так же, как ошибку самого прохода в этом месте. Внутренний: подать разделу статус или пропажу связи может только
/// наблюдатель (и тесты).
/// </summary>
internal interface IServerMonitorHost
{
    /// <summary>Профиль, за сервером которого следим (активный); null — следить не за чем, сеанс кончается.</summary>
    GameProfile? Profile { get; }

    /// <summary>Состояние сервера, как его сейчас показывает раздел (его меняют и кнопки, не только статус агента).</summary>
    ServerState State { get; }

    /// <summary>Идёт действие с кнопки — устаревшего агента сейчас не меняем.</summary>
    bool IsBusy { get; }

    /// <summary>Связь с агентом появилась (<see cref="ServerMonitor.Client"/> — уже она): пора запускать поток консоли.</summary>
    void OnAttached(AgentClient client, CancellationToken session);

    /// <summary>Связь с агентом пропала: клиент закрыт, <see cref="ServerMonitor.Client"/> — null.</summary>
    void OnDetached();

    /// <summary>Свежий статус агента.</summary>
    void OnStatus(AgentStatus status);

    /// <summary>
    /// Свой сервер: проход закончен. <paramref name="serverPids"/> — серверы из папки игры профиля (свой и чужие),
    /// <paramref name="ourServerUp"/> — работал ли сервер под нашим агентом перед поиском: тогда его PID среди найденных не чужой.
    /// </summary>
    void OnLocalChecked(IReadOnlyList<int> serverPids, bool ourServerUp);

    /// <summary>
    /// Удалённый агент ответил на опрос (статус уже передан): прежнюю ошибку связи пора снять. Зовётся там же, где
    /// берётся статус, поэтому сбой здесь разбирается как пропажа связи, а не как ошибка прохода.
    /// </summary>
    void OnRemoteAnswered();

    /// <summary>Удалённый сервер: проход закончен. null — статус получен; иначе — почему связи нет (клиент уже закрыт).</summary>
    void OnRemoteChecked(Exception? error);

    /// <summary>Код подключения не расшифровать (другой пользователь или компьютер): ждать нечего, сеанс кончается.</summary>
    void OnRemoteCodeUnreadable();
}

/// <summary>
/// Связь раздела «Сервер» с агентом сервера активного профиля: один клиент агента и один цикл опроса на сеанс (сеанс —
/// от смены профиля до следующей). Свой сервер: подхватить агента, когда он появится, брать его статус раз в полторы
/// секунды, заменить устаревшего агента, пока сервер стоит, искать сервер из той же папки игры, запущенный не нами.
/// Удалённый: подключиться по коду и брать статус; связь пропала — новая попытка через пять секунд. Что показывать по
/// статусу и ошибкам, решает хозяин (<see cref="IServerMonitorHost"/>).
/// </summary>
internal sealed class ServerMonitor(IServerMonitorHost host)
{
    private CancellationTokenSource? _session;

    /// <summary>Связь с агентом сервера профиля (своего или удалённого); null — агента нет или связи нет.</summary>
    public AgentClient? Client { get; private set; }

    /// <summary>Сеанс прежнего профиля закончен: опрос останавливается, связь с его агентом закрывается.</summary>
    public void Stop()
    {
        _session?.Cancel();
        Client?.Dispose();
        Client = null;
    }

    /// <summary>Начать сеанс слежения за сервером профиля. Возвращает задачу цикла: тестам — дождаться конца сеанса.</summary>
    public Task Start()
    {
        _session = new CancellationTokenSource();
        return WatchAsync(_session.Token);
    }

    /// <summary>
    /// Связь, поднятая не циклом (кнопка «Запустить» запустила агента), — в текущий сеанс. Только после
    /// <see cref="Start"/>: кнопка доступна у профиля сервера, а для него сеанс уже начат. Отдельной проверки нет
    /// намеренно: другое исключение кнопка поймала бы и показала строкой ошибки, а раньше такого не было.
    /// </summary>
    public void Attach(AgentClient client) => Attach(client, _session!.Token);

    private void Attach(AgentClient client, CancellationToken ct)
    {
        Client = client;
        host.OnAttached(client, ct);
    }

    private void Detach()
    {
        Client?.Dispose();
        Client = null;
        host.OnDetached();
    }

    /// <summary>Цикл сеанса: подхватить агента, если он появился; следить за статусом; искать чужой сервер.</summary>
    private async Task WatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var profile = host.Profile;
                if (profile is null || profile.IsHosted) return; // у хостера агента нет: нечего слушать

                if (profile.IsRemote)
                {
                    if (!await WatchRemoteAsync(profile, ct)) return;
                    continue;
                }

                if (Client is null && AgentClient.TryConnect(profile.Id) is { } found)
                    Attach(found, ct);

                if (Client is not null)
                {
                    try
                    {
                        var status = await Client.StatusAsync(ct);
                        host.OnStatus(status);
                        // eViSTool обновили, а агент прежний: сервер стоит — меняем агента (нужен для удалённого доступа — сразу
                        // запускаем новый; нет — новый появится при следующем запуске сервера)
                        if (AgentProtocol.IsOutdated(status) && status.State == ServerState.Stopped && status.RestartScheduledAt is null && !host.IsBusy)
                        {
                            var old = Client;
                            Detach();
                            await AgentLauncher.ReplaceAsync(old, ct);
                            if (RemoteAccess.Load(profile.Id).Enabled)
                                using (await AgentLauncher.EnsureRunningAsync(profile, startServer: false, ct: ct)) { }
                        }
                    }
                    catch (HttpRequestException)
                    {
                        Detach(); // агент ушёл (например, сам завершился после простоя)
                    }
                    catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException) { }
                }

                // чужой сервер: из папки игры профиля, но не наш
                var ours = Client is not null ? host.State != ServerState.Stopped : false;
                var pids = await Task.Run(() => GameProcess.FindServerPids(profile), ct);
                if (ct.IsCancellationRequested) return; // профиль уже сменили — найденное относится не к нему
                host.OnLocalChecked(pids, ours);

                try { await Task.Delay(1500, ct); } catch (TaskCanceledException) { return; }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OperationCanceledException)
            {
                // неожиданная ошибка (битый ответ, файл занят, процесс не прочитать) не должна молча останавливать опрос:
                // иначе вкладка застынет на старом состоянии. Пишем в журнал и пробуем снова
                Ui.Platform.WriteCrashLog(ex);
                try { await Task.Delay(3000, ct); } catch (TaskCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// Один проход слежения за удалённым сервером: подключиться по коду (если ещё нет) и взять статус.
    /// Связь пропала — хозяину ошибка и новая попытка через несколько секунд. false — сеанс закончен.
    /// </summary>
    private async Task<bool> WatchRemoteAsync(GameProfile profile, CancellationToken ct)
    {
        Exception? error = null;
        try
        {
            if (Client is null)
            {
                if (RemoteSecret.Unprotect(profile.RemoteCode) is not { } code)
                {
                    host.OnRemoteCodeUnreadable();
                    return false; // без кода ждать нечего: его нужно вставить заново в настройках
                }
                var client = AgentClient.ForRemote(code);
                try
                {
                    var status = await client.StatusAsync(ct);
                    Attach(client, ct);
                    host.OnStatus(status);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            }
            else
            {
                host.OnStatus(await Client.StatusAsync(ct));
            }
            host.OnRemoteAnswered();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) return false;
            if (Client is not null) Detach();
            error = ex;
        }
        host.OnRemoteChecked(error);
        try { await Task.Delay(Client is null ? 5000 : 1500, ct); } catch (TaskCanceledException) { return false; }
        return true;
    }
}

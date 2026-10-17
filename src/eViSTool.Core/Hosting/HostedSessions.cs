namespace eViSTool.Core.Hosting;

/// <summary>
/// Соединения с серверами у хостера: одно на профиль, операции профиля строго по очереди, закрывается после минуты
/// простоя; после обрыва — один повтор, и только для чтения (шаги записи вслепую не повторяются).
/// <para>
/// Очередь не реентерабельна: работа, начатая в <see cref="UseAsync{T}(string, HostedEndpoint, Func{IRemoteFiles, Task{T}}, CancellationToken)"/>,
/// не зовёт её снова для того же профиля, а работает с уже выданным соединением.
/// </para>
/// </summary>
public static class HostedSessions
{
    /// <summary>Сколько соединение живёт без дела.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Session> Sessions = new(StringComparer.Ordinal);

    /// <summary>Выполнить работу на соединении профиля (взять живое или открыть новое), по очереди с остальными.
    /// Обрыв не повторяется: так безопасно для записи.</summary>
    public static Task<T> UseAsync<T>(string profileId, HostedEndpoint endpoint, Func<IRemoteFiles, Task<T>> work, CancellationToken ct) =>
        UseAsync(profileId, endpoint, work, readOnly: false, ct);

    /// <summary>
    /// То же; <paramref name="readOnly"/> — работа только читает, и после обрыва соединения её можно один раз повторить
    /// на новом.
    /// </summary>
    public static async Task<T> UseAsync<T>(string profileId, HostedEndpoint endpoint, Func<IRemoteFiles, Task<T>> work, bool readOnly,
        CancellationToken ct)
    {
        var session = SessionFor(profileId);
        await session.Turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            session.StopIdle();
            for (var attempt = 0; ; attempt++)
            {
                var files = await session.OpenAsync(endpoint, ct).ConfigureAwait(false);
                try
                {
                    return await work(files).ConfigureAwait(false);
                }
                catch (RemoteFilesException ex) when (IsDrop(ex) || !files.IsConnected)
                {
                    // соединение больше не годится: следующая операция откроет новое
                    await session.CloseAsync().ConfigureAwait(false);
                    if (!readOnly || attempt > 0 || ct.IsCancellationRequested) throw;
                }
                catch when (!files.IsConnected)
                {
                    await session.CloseAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            if (session.Forgotten) await session.CloseAsync().ConfigureAwait(false);
            else session.StartIdle();
            session.Turn.Release();
        }
    }

    /// <summary>Закрыть и забыть соединение профиля (профиль удалили или сменили подключение).</summary>
    public static void Forget(string profileId)
    {
        Session? session;
        lock (Gate)
        {
            if (!Sessions.Remove(profileId, out session)) return;
        }
        session.Forgotten = true;
        _ = session.CloseWhenFreeAsync();
    }

    /// <summary>Обрыв или молчание: соединение, скорее всего, мёртвое.</summary>
    private static bool IsDrop(RemoteFilesException ex) => ex.Kind is RemoteFailure.Connect or RemoteFailure.Timeout;

    private static Session SessionFor(string profileId)
    {
        lock (Gate)
        {
            if (!Sessions.TryGetValue(profileId, out var session)) Sessions[profileId] = session = new Session();
            return session;
        }
    }

    private sealed class Session
    {
        public readonly SemaphoreSlim Turn = new(1, 1);
        public volatile bool Forgotten;
        private IRemoteFiles? _files;
        private HostedEndpoint? _endpoint;
        private Timer? _idle;

        /// <summary>Живое соединение с тем же адресом и паролем — или новое. Вызывается только под <see cref="Turn"/>.</summary>
        public async Task<IRemoteFiles> OpenAsync(HostedEndpoint endpoint, CancellationToken ct)
        {
            if (_files is { IsConnected: true } alive && endpoint.Equals(_endpoint)) return alive;
            await CloseAsync().ConfigureAwait(false);
            var files = RemoteFilesFactory.Create(endpoint);
            try
            {
                await files.ConnectAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await DisposeQuietlyAsync(files).ConfigureAwait(false);
                throw;
            }
            _files = files;
            _endpoint = endpoint;
            return files;
        }

        public async Task CloseAsync()
        {
            var files = _files;
            _files = null;
            _endpoint = null;
            if (files is not null) await DisposeQuietlyAsync(files).ConfigureAwait(false);
        }

        public void StopIdle()
        {
            _idle?.Dispose();
            _idle = null;
        }

        public void StartIdle()
        {
            StopIdle();
            if (_files is null) return;
            _idle = new Timer(_ => _ = CloseIfIdleAsync(), null, IdleTimeout, Timeout.InfiniteTimeSpan);
        }

        private async Task CloseIfIdleAsync()
        {
            // занято — значит, не простаивает: таймер перезапустит та операция
            if (!await Turn.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                await CloseAsync().ConfigureAwait(false);
            }
            finally
            {
                Turn.Release();
            }
        }

        public async Task CloseWhenFreeAsync()
        {
            await Turn.WaitAsync().ConfigureAwait(false);
            try
            {
                StopIdle();
                await CloseAsync().ConfigureAwait(false);
            }
            finally
            {
                Turn.Release();
            }
        }

        private static async Task DisposeQuietlyAsync(IRemoteFiles files)
        {
            try
            {
                await files.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // соединение и так уходит; ошибка закрытия никому не нужна
            }
        }
    }
}

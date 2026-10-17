namespace eViSTool.Core.Hosting;

/// <summary>
/// Соединения с серверами у хостера: одно на профиль, операции профиля строго по очереди, закрывается после минуты
/// простоя; после обрыва — один повтор, и только для чтения.
/// </summary>
public static class HostedSessions
{
    /// <summary>Выполнить работу на соединении профиля (взять живое или открыть новое), по очереди с остальными.</summary>
    public static Task<T> UseAsync<T>(string profileId, HostedEndpoint endpoint, Func<IRemoteFiles, Task<T>> work, CancellationToken ct) =>
        // shortcut: заглушка до пакета P3; ошибка ловится фильтрами окна, в отличие от NotImplementedException
        Task.FromException<T>(new RemoteFilesException(RemoteFailure.Unsupported, "HostedSessions.UseAsync is not implemented yet"));

    /// <summary>Закрыть и забыть соединение профиля (профиль удалили или сменили подключение).</summary>
    public static void Forget(string profileId)
    {
        // shortcut: соединений пока не бывает — забывать нечего; пул соединений — пакет P3, там и наполнить
    }
}

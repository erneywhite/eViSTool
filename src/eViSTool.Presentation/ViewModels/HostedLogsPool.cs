using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Логи серверов у хостера — один <see cref="HostedLogs"/> на профиль на всё окно: раздел «Сервер» его опрашивает,
/// а перед изменением модов он же быстро проверяет, остановлен ли сервер. Строки, которые прочитала проверка, раздел
/// получит со следующим опросом. Поменяли подключение профиля (адрес, логин, пароль, корень, путь к конфигу) — экземпляр
/// новый: прежний читал другой сервер, и его позиция в логе к новому не относится.
/// </summary>
internal static class HostedLogsPool
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, HostedLogs> ByProfile = new(StringComparer.Ordinal);

    /// <summary>Логи сервера цели (цель — у хостера).</summary>
    public static HostedLogs For(ModTarget target)
    {
        lock (Gate)
        {
            if (ByProfile.TryGetValue(target.ProfileId, out var logs) && SameServer(logs.Target, target)) return logs;
            return ByProfile[target.ProfileId] = new HostedLogs(target);
        }
    }

    /// <summary>Тот же сервер и то же место логов: подключение и пути, от которых зависит папка Logs.</summary>
    public static bool SameServer(ModTarget a, ModTarget b) =>
        a.ProfileId == b.ProfileId && a.Hosted is { } x && b.Hosted is { } y && x.Endpoint == y.Endpoint
        && x.Settings.Root == y.Settings.Root && x.Settings.ConfigPath == y.Settings.ConfigPath;
}

using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;

namespace eViSTool.Presentation.ViewModels.Mods;

/// <summary>
/// Вопрос «останови сервер в панели хостера» перед изменением модов. Запущенный сервер новые моды не увидит, а при
/// остановке может перезаписать serverconfig.json; запущен ли он, по FTP не узнать, поэтому спрашиваем человека.
/// «Да» помнится <see cref="Memory"/> для профиля (серия операций подряд не переспрашивается); флажок профиля
/// «Напоминать остановить сервер» выключает вопрос совсем. Перед вопросом можно проверить лог сервера
/// (<see cref="CheckAsync"/>): по логу остановлен — спрашивать незачем.
/// </summary>
/// <param name="check">Быстрая проверка по логу; по умолчанию — <see cref="HostedLogs.CheckNowAsync"/> логов профиля
/// (<see cref="HostedLogsPool"/>). Тесты подменяют.</param>
internal sealed class HostedStopReminder(Func<DateTime>? now = null, Func<ModTarget, CancellationToken, Task<HostedServerState>>? check = null)
{
    /// <summary>Сколько помнится ответ «Да».</summary>
    public static readonly TimeSpan Memory = TimeSpan.FromMinutes(10);

    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    private readonly Dictionary<string, DateTime> _until = new(StringComparer.Ordinal);

    /// <summary>true — можно менять моды: сервер остановлен (ответили «Да» сейчас или недавно) или вопрос выключен.</summary>
    public bool Confirm(ModTarget target)
    {
        if (target.Hosted?.Settings.SkipStopReminder == true) return true;
        if (_until.TryGetValue(target.ProfileId, out var until) && _now() < until) return true;
        if (!Ui.Confirm(Loc.T("hosted.stopAsk", target.Name), byDefault: Answer.No, warning: true)) return false;
        _until[target.ProfileId] = _now() + Memory;
        return true;
    }

    // ---------- проверка по логу сервера

    /// <summary>Сколько верить проверке «по логу остановлен»: вопросам той же операции, что её запустила, не дольше.</summary>
    public static readonly TimeSpan StoppedMemory = TimeSpan.FromSeconds(30);

    private readonly Func<ModTarget, CancellationToken, Task<HostedServerState>> _check =
        check ?? ((target, ct) => HostedLogsPool.For(target).CheckNowAsync(ct));

    private readonly Dictionary<string, DateTime> _stoppedUntil = new(StringComparer.Ordinal);

    /// <summary>
    /// Быстрая проверка по логу сервера перед вопросом: асинхронная (окно её не ждёт) и не дольше
    /// <see cref="HostedLogs.QuickTimeout"/>. Вопрос выключен или «Да» ещё помнится — в сеть не ходим. По логу
    /// «остановлен» — запоминаем на <see cref="StoppedMemory"/>, и <see cref="IsKnownStopped"/> скажет «не спрашивать».
    /// Запущен, запускается, останавливается, вылетел или узнать не вышло — вопрос как раньше: лог убитого сервера
    /// «застывает» запущенным, а вылетевший сервер панель хостера может тут же запустить снова.
    /// </summary>
    public async Task CheckAsync(ModTarget target, CancellationToken ct = default)
    {
        if (!target.IsHosted || !WouldAsk(target)) return;
        HostedServerState state;
        try
        {
            state = await _check(target, ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            state = HostedServerState.Unknown; // сеть или раскладка сервера — не узнали, спросим
        }
        if (state == HostedServerState.Stopped) _stoppedUntil[target.ProfileId] = _now() + StoppedMemory;
        else _stoppedUntil.Remove(target.ProfileId);
    }

    /// <summary>Только что проверили по логу: сервер остановлен — спрашивать не надо.</summary>
    public bool IsKnownStopped(ModTarget target) => _stoppedUntil.TryGetValue(target.ProfileId, out var until) && _now() < until;

    /// <summary>Вопрос был бы задан: он не выключен в профиле и «Да» уже не помнится.</summary>
    private bool WouldAsk(ModTarget target) =>
        target.Hosted?.Settings.SkipStopReminder != true && !(_until.TryGetValue(target.ProfileId, out var until) && _now() < until);
}

using eViSTool.Core.Mods;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Обновления модов сервера у хостера, найденные проверкой: когда проверяли (UTC) и что можно поставить.</summary>
public sealed record HostedUpdatesFound(DateTime CheckedAt, IReadOnlyList<ModCheckResult> Due);

/// <summary>
/// Проверки обновлений модов серверов у хостера — одни на окно: сторож оповещений проверяет раз в 6 часов, плашка
/// «Обновить всё» раздела «Сервер» берёт свежую проверку (не старше 30 минут) или проверяет сама. Проверка — тем же
/// путём, что «Проверить» в «Моих модах» (<see cref="ModUpdatesDue"/>: скан по FTP, модбаза), без скачивания. Проверки
/// одного профиля, начатые одновременно, сливаются в одну. Всё — в потоке окна.
/// </summary>
/// <param name="check">Сама проверка (тесты подменяют, чтобы не ходить в модбазу).</param>
public sealed class HostedModUpdates(Func<ModTarget, CancellationToken, Task<IReadOnlyList<ModCheckResult>>> check, TimeProvider? time = null)
{
    private readonly Dictionary<string, HostedUpdatesFound> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<HostedUpdatesFound>> _running = new(StringComparer.Ordinal);

    /// <summary>Часы (UTC) для отметки «когда проверяли».</summary>
    public TimeProvider Time { get; } = time ?? TimeProvider.System;

    /// <summary>Прежняя проверка устарела (моды профиля поменяли) или появилась новая.</summary>
    public event Action<string>? Changed;

    /// <summary>Последняя проверка профиля; не проверяли (или моды с тех пор меняли) — null.</summary>
    public HostedUpdatesFound? Known(string profileId) => _known.GetValueOrDefault(profileId);

    /// <summary>Последняя проверка, если она не старше <paramref name="age"/>; иначе null.</summary>
    public HostedUpdatesFound? Fresh(string profileId, TimeSpan age) =>
        Known(profileId) is { } found && Time.GetUtcNow().UtcDateTime - found.CheckedAt <= age ? found : null;

    /// <summary>Проверить сейчас; та же проверка профиля уже идёт — дождаться её. Сбой сети или модбазы — исключение.</summary>
    public Task<HostedUpdatesFound> CheckAsync(ModTarget target, CancellationToken ct = default)
    {
        if (_running.TryGetValue(target.ProfileId, out var running)) return running;
        var task = RunAsync(target, ct);
        if (!task.IsCompleted) _running[target.ProfileId] = task;
        return task;
    }

    /// <summary>Моды профиля поменялись (их обновили, поставили, откатили): прежняя проверка уже неправда.</summary>
    public void Forget(string profileId)
    {
        if (_known.Remove(profileId)) Changed?.Invoke(profileId);
    }

    private async Task<HostedUpdatesFound> RunAsync(ModTarget target, CancellationToken ct)
    {
        try
        {
            var due = await check(target, ct);
            var found = new HostedUpdatesFound(Time.GetUtcNow().UtcDateTime, due);
            _known[target.ProfileId] = found;
            Changed?.Invoke(target.ProfileId);
            return found;
        }
        finally
        {
            _running.Remove(target.ProfileId);
        }
    }
}

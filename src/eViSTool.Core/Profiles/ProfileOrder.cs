namespace eViSTool.Core.Profiles;

/// <summary>Порядок профилей в списке «Профилей» и в переключателе в шапке.</summary>
public enum ProfileSort { Created, Name, LastPlayed }

/// <summary>
/// Сравнение профилей для <see cref="ProfileSort"/>. «Как созданы» — порядок в настройках (новый дописывается в конец),
/// поэтому сравнение там не нужно: null. «Последняя игра» — сначала недавние, ни разу не запущенные — в конце в порядке
/// создания.
/// </summary>
public static class ProfileOrder
{
    public static Comparison<GameProfile>? For(ProfileSort sort, Func<GameProfile, DateTime?> lastPlayed) => sort switch
    {
        ProfileSort.Name => (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
        ProfileSort.LastPlayed => (a, b) => (lastPlayed(a), lastPlayed(b)) switch
        {
            ({ } x, { } y) => y.CompareTo(x),
            (not null, null) => -1,
            (null, not null) => 1,
            _ => 0,
        },
        _ => null,
    };

    /// <summary>
    /// Когда в профиле последний раз играли: запуск из окна (<see cref="GameProfile.LastPlayed"/>) или, если позже,
    /// последняя запись игры в её лог — так видны и запуски мимо eViSTool, и профили, созданные до этой отметки.
    /// У удалённого сервера и сервера у хостера логов здесь нет — только запуск из окна.
    /// </summary>
    public static DateTime? LastPlayed(GameProfile profile)
    {
        var stamp = profile.LastPlayed;
        if (profile.IsLocal && !string.IsNullOrEmpty(profile.DataDir))
        {
            var log = Path.Combine(profile.DataDir, "Logs", profile.Kind == ProfileKind.Server ? "server-main.log" : "client-main.log");
            try
            {
                if (File.Exists(log) && File.GetLastWriteTime(log) is var written && (stamp is null || written > stamp)) stamp = written;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return stamp;
    }
}

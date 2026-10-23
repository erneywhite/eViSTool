using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.Core.Mods;

/// <summary>
/// Куда ставится мод — запоминается в момент, когда пользователь нажал «Обновить» / «Установить», и дальше не меняется.
/// Операция работает только с этой целью и не смотрит на активный профиль: его могут переключить, пока идёт загрузка.
/// Свой профиль — по папкам на этой машине, удалённый сервер — по коду подключения (своё соединение на операцию),
/// сервер у хостера — по FTP (<see cref="Hosted"/>).
/// </summary>
public sealed record ModTarget(GameProfile Profile, ConnectionCode? Remote, HostedTarget? Hosted = null)
{
    public string ProfileId => Profile.Id;
    public string Name => Profile.Name;
    public bool IsRemote => Remote is not null;
    public bool IsHosted => Hosted is not null;
    public bool IsLocal => Remote is null && Hosted is null;

    /// <summary>
    /// Цель для профиля — снимок на этот момент: правка профиля в настройках посреди загрузки её уже не сдвинет.
    /// Удалённый — с расшифрованным кодом (null — код не прочитать на этой машине). У хостера — с расшифрованным паролем
    /// (null — подключение не задано или пароль не прочитать). Почему null — <see cref="UnreadableText"/>.
    /// </summary>
    public static ModTarget? For(GameProfile profile)
    {
        var snapshot = new GameProfile
        {
            Id = profile.Id, Name = profile.Name, Kind = profile.Kind,
            GameDir = profile.GameDir, DataDir = profile.DataDir, RemoteCode = profile.RemoteCode,
            Hosted = profile.Hosted?.Clone(),
            PinnedMods = new(profile.PinnedMods, StringComparer.OrdinalIgnoreCase),
            BlockedVersions = profile.BlockedVersions.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase),
        };
        if (snapshot.IsHosted)
            return snapshot.Hosted is { } settings && HostedEndpoint.From(settings) is { } endpoint
                ? new ModTarget(snapshot, null, new HostedTarget(endpoint, settings))
                : null;
        if (snapshot.IsLocal) return new ModTarget(snapshot, null);
        return RemoteSecret.Unprotect(snapshot.RemoteCode) is { } code ? new ModTarget(snapshot, code) : null;
    }

    /// <summary>Почему <see cref="For"/> вернул null — словами для окна.</summary>
    public static string UnreadableText(GameProfile profile) =>
        !profile.IsHosted ? Loc.T("remote.errDecrypt")
        : profile.Hosted is null ? Loc.T("hosted.errNoConnection")
        : Loc.T("hosted.errDecrypt");

    /// <summary>Без секретов: ни ключа агента из кода подключения, ни пароля FTP.</summary>
    public override string ToString() =>
        Hosted is { } hosted ? $"{Name} [hosted {hosted}]"
        : Remote is { } code ? $"{Name} [agent {code.Host}:{code.Port}]"
        : $"{Name} [local]";
}

/// <summary>Сервер у хостера для операции: куда подключаться (с паролем, только в памяти) и снимок настроек профиля.</summary>
public sealed record HostedTarget(HostedEndpoint Endpoint, HostedConnection Settings)
{
    /// <summary>Без пароля.</summary>
    public override string ToString() => Endpoint.ToString();
}

/// <summary>Итог установки одного архива.</summary>
public sealed record ModInstallOutcome(string Name, string? Version, string? OldVersion)
{
    /// <summary>«Carry On: 1.0 → 1.1» или «Carry On 1.1».</summary>
    public string Text => OldVersion is not null ? $"{Name}: {OldVersion} → {Version}" : $"{Name} {Version}";
}

/// <summary>
/// Операции с модами цели: что стоит, установка архива, включение и выключение. Логика одна для своего профиля
/// и удалённого сервера (там то же самое делает его агент — см. <see cref="ServerMods"/>); у сервера у хостера —
/// <see cref="HostedMods"/>. Ветка хостера — первой строкой: иначе FTP-цель ушла бы в ветку своего профиля.
/// </summary>
public static class ModTargets
{
    /// <summary>
    /// Профиль цели и её моды — с диска, у агента или по FTP. Удалённый профиль получает папки и версию игры от сервера.
    /// <paramref name="progress"/> — строки «читаю моды с сервера» (пока только у хостера: первое чтение там долгое).
    /// </summary>
    public static async Task<(ResolvedProfile Profile, IReadOnlyList<LocalMod> Mods)> ScanAsync(ModTarget target, CancellationToken ct = default,
        IProgress<string>? progress = null)
    {
        if (target.IsHosted) return await new HostedMods(target).ScanAsync(progress, ct).ConfigureAwait(false);
        if (target.Remote is not { } code)
        {
            var resolved = ProfileResolver.Resolve(target.Profile);
            return (resolved, await Task.Run(() => ModUpdateService.ScanLocal(resolved), ct).ConfigureAwait(false));
        }
        using var agent = AgentClient.ForRemote(code);
        var list = await agent.ModsAsync(ct).ConfigureAwait(false);
        return Remote(target.Profile, list);
    }

    /// <summary>Ответ агента → профиль и моды в том же виде, что у своей машины.</summary>
    public static (ResolvedProfile Profile, IReadOnlyList<LocalMod> Mods) Remote(GameProfile profile, RemoteModList list)
    {
        var resolved = new ResolvedProfile
        {
            Profile = profile,
            GameVersion = Versioning.ModVersion.ParseOrNull(list.GameVersion),
            ModDirs = list.ModDirs,
            InstallDir = list.InstallDir,
            DisabledMods = list.DisabledMods,
            OffMachine = true, // пути — на машине агента
        };
        var disabled = new HashSet<string>(list.DisabledMods);
        var mods = list.Mods.Select(m => new LocalMod(m.Path, m.Info, m.Error) { Release = m.Release })
            .Select(l => l with { IsDisabled = ModUpdateService.IsDisabled(l, disabled) })
            .ToList();
        return (resolved, mods);
    }

    /// <summary>
    /// Поставить архив в цель: прежняя версия — в хранилище для отката, выключенный мод остаётся выключенным.
    /// <paramref name="confirm"/> видит план (та же версия, даунгрейд) и может отказаться — тогда null, ничего не изменено.
    /// <paramref name="release"/> — релиз ModDB, из которого скачан архив (null — архив не с ModDB): версия в файле не та,
    /// что на ModDB, — отметка «файл релиза» (<see cref="ReleaseMarks"/>) там, где мод лежит: у своего профиля и у хостера —
    /// на этой машине, у удалённого сервера — у его агента.
    /// </summary>
    public static async Task<ModInstallOutcome?> InstallAsync(ModTarget target, string zip, Func<InstallPlan, bool>? confirm = null,
        CancellationToken ct = default, ModDbRelease? release = null)
    {
        if (target.IsHosted) return await new HostedMods(target).InstallAsync(zip, confirm, ct, release).ConfigureAwait(false);
        var (resolved, mods) = await ScanAsync(target, ct).ConfigureAwait(false);
        var plan = ModInstaller.Plan(zip, resolved, mods);
        if (confirm is not null && !confirm(plan)) return null;

        if (target.Remote is { } code)
        {
            // удалённому серверу план нужен только для вопросов: ставит его агент, по тем же правилам
            using var agent = AgentClient.ForRemote(code);
            var result = await agent.InstallModAsync(zip, ct, release).ConfigureAwait(false);
            return new ModInstallOutcome(result.Name, result.Version, result.OldVersion);
        }

        var info = plan.Incoming.Info!;
        var disabled = new HashSet<string>(resolved.DisabledMods);
        var wasDisabled = plan.Replaces.Any(r => ModUpdateService.IsDisabled(r, disabled));
        ModInstaller.Apply(plan, ModBackupStore.ForProfile(target.Profile), release);
        if (wasDisabled) ModConfigEditor.SetEnabled(ProfileResolver.Resolve(target.Profile), info, enabled: false);
        return new ModInstallOutcome(info.Name, info.Version, plan.Replaces.FirstOrDefault()?.Info?.Version);
    }

    /// <summary>
    /// История изменений модов цели (записи и отметки запусков): своя и у хостера — из файла на этой машине,
    /// удалённого сервера — у агента.
    /// </summary>
    public static async Task<IReadOnlyList<ModHistoryEntry>> HistoryAsync(ModTarget target, CancellationToken ct = default)
    {
        if (target.Remote is not { } code)
            return await Task.Run(() => ModHistory.ReadEntries(ModHistory.FileFor(target.ProfileId)), ct).ConfigureAwait(false);
        using var agent = AgentClient.ForRemote(code);
        return await agent.ModHistoryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Включить или выключить мод цели — так же, как это делает менеджер модов игры.</summary>
    public static async Task SetEnabledAsync(ModTarget target, LocalMod mod, bool enabled, CancellationToken ct = default)
    {
        if (target.IsHosted)
        {
            await new HostedMods(target).SetEnabledAsync(mod.Path, enabled, ct).ConfigureAwait(false);
            return;
        }
        if (target.Remote is { } code)
        {
            using var agent = AgentClient.ForRemote(code);
            await agent.SetModEnabledAsync(mod.Path, enabled, ct).ConfigureAwait(false);
            return;
        }
        ModConfigEditor.SetEnabled(ProfileResolver.Resolve(target.Profile), mod.Info ?? throw new InvalidOperationException(mod.Error ?? mod.Path), enabled);
    }

    /// <summary>
    /// Удалить мод цели: свой — в корзину (<paramref name="moveToTrash"/>), удалённого сервера — в корзину на его машине,
    /// у хостера — копия на этой машине, потом удаление на сервере. Запись в историю; последняя копия мода — и из
    /// списка выключенных.
    /// </summary>
    public static async Task DeleteAsync(ModTarget target, LocalMod mod, Action<string> moveToTrash, CancellationToken ct = default)
    {
        if (target.IsHosted)
        {
            await new HostedMods(target).DeleteAsync(mod.Path, ct).ConfigureAwait(false);
            return;
        }
        if (target.Remote is { } code)
        {
            using var agent = AgentClient.ForRemote(code);
            await agent.DeleteModAsync(mod.Path, ct).ConfigureAwait(false); // на сервере — в его корзину (и в его историю)
            return;
        }
        moveToTrash(mod.Path);
        if (mod.Info is not { } gone) return;
        ModHistory.Record(ModHistory.FileFor(target.ProfileId), gone.ModId, gone.Name, gone.Version, null);
        // других копий мода не осталось — убираем его и из списка выключенных
        var resolved = ProfileResolver.Resolve(target.Profile);
        var left = await Task.Run(() => ModUpdateService.ScanLocal(resolved), ct).ConfigureAwait(false);
        if (left.All(l => l.Info?.ModId != gone.ModId)) ModConfigEditor.Forget(resolved, gone);
    }

    /// <summary>
    /// Отпечаток набора модов — для опроса «поменяли со стороны». Свой — по времени папок модов и конфига, у хостера —
    /// по листингу; у агента — null (он сам сообщает об изменениях в статусе).
    /// </summary>
    public static async Task<string?> StampAsync(ModTarget target, CancellationToken ct = default)
    {
        if (target.IsHosted) return await new HostedMods(target).StampAsync(ct).ConfigureAwait(false);
        if (target.IsRemote) return null;
        var stamp = await Task.Run(() => ServerMods.StampOf(ProfileResolver.Resolve(target.Profile)), ct).ConfigureAwait(false);
        return stamp?.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Ошибка операции с целью — словами: у агента и хостера свои тексты, у своего профиля — текст исключения.</summary>
    public static string Describe(ModTarget target, Exception ex) =>
        target.IsHosted ? HostedSecret.Describe(ex)
        : target.IsRemote ? RemoteSecret.Describe(ex)
        : ex.Message;

    /// <summary>Удалённые серверы, не ответившие проверке «Установить также в»: id профиля → когда и почему.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, Exception Error)> Unreachable = new();

    /// <summary>Сколько помнить, что удалённый сервер не ответил: выключенную машину не ждать при каждой установке.</summary>
    public static readonly TimeSpan UnreachableMemory = TimeSpan.FromMinutes(2);

    /// <summary>Удалённый сервер этой цели недавно не ответил проверке — пока не спрашиваем его снова.</summary>
    public static bool RecentlyUnreachable(ModTarget target) =>
        target.IsRemote && Unreachable.TryGetValue(target.ProfileId, out var miss) && DateTime.UtcNow - miss.At < UnreachableMemory;

    /// <summary>
    /// Скан цели для проверки «Установить также в». Удалённый сервер, недавно не ответивший, не спрашиваем: сразу та же
    /// ошибка, и профиль в окне выбора — «нет связи». Ответил — забываем отказ. Своя установка мода идёт мимо этого.
    /// </summary>
    public static async Task<(ResolvedProfile Profile, IReadOnlyList<LocalMod> Mods)> ScanForOfferAsync(ModTarget target, CancellationToken ct = default)
    {
        if (!target.IsRemote) return await ScanAsync(target, ct).ConfigureAwait(false);
        if (RecentlyUnreachable(target) && Unreachable.TryGetValue(target.ProfileId, out var miss))
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(miss.Error);
        try
        {
            var scanned = await ScanAsync(target, ct).ConfigureAwait(false);
            Unreachable.TryRemove(target.ProfileId, out _);
            return scanned;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            Unreachable[target.ProfileId] = (DateTime.UtcNow, ex);
            throw;
        }
    }

    /// <summary>Сколько ждать скан цели в фоновых проверках («Установить также в»): у хостера первое чтение долгое.</summary>
    public static TimeSpan ScanTimeout(ModTarget target) => target.IsHosted ? TimeSpan.FromSeconds(45) : TimeSpan.FromSeconds(10);
}

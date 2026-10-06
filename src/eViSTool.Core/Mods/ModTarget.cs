using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.Core.Mods;

/// <summary>
/// Куда ставится мод — запоминается в момент, когда пользователь нажал «Обновить» / «Установить», и дальше не меняется.
/// Операция работает только с этой целью и не смотрит на активный профиль: его могут переключить, пока идёт загрузка.
/// Свой профиль — по папкам на этой машине, удалённый сервер — по коду подключения (своё соединение на операцию).
/// </summary>
public sealed record ModTarget(GameProfile Profile, ConnectionCode? Remote)
{
    public string ProfileId => Profile.Id;
    public string Name => Profile.Name;
    public bool IsRemote => Remote is not null;

    /// <summary>
    /// Цель для профиля — снимок на этот момент: правка профиля в настройках посреди загрузки её уже не сдвинет.
    /// Удалённый — с расшифрованным кодом (null — код не прочитать на этой машине).
    /// </summary>
    public static ModTarget? For(GameProfile profile)
    {
        var snapshot = new GameProfile
        {
            Id = profile.Id, Name = profile.Name, Kind = profile.Kind,
            GameDir = profile.GameDir, DataDir = profile.DataDir, RemoteCode = profile.RemoteCode,
            PinnedMods = new(profile.PinnedMods, StringComparer.OrdinalIgnoreCase),
            BlockedVersions = profile.BlockedVersions.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase),
        };
        if (!snapshot.IsRemote) return new ModTarget(snapshot, null);
        return RemoteSecret.Unprotect(snapshot.RemoteCode) is { } code ? new ModTarget(snapshot, code) : null;
    }
}

/// <summary>Итог установки одного архива.</summary>
public sealed record ModInstallOutcome(string Name, string? Version, string? OldVersion)
{
    /// <summary>«Carry On: 1.0 → 1.1» или «Carry On 1.1».</summary>
    public string Text => OldVersion is not null ? $"{Name}: {OldVersion} → {Version}" : $"{Name} {Version}";
}

/// <summary>
/// Операции с модами цели: что стоит, установка архива, включение и выключение. Логика одна для своего профиля
/// и удалённого сервера (там то же самое делает его агент — см. <see cref="ServerMods"/>).
/// </summary>
public static class ModTargets
{
    /// <summary>Профиль цели и её моды — с диска или у агента. Удалённый профиль получает папки и версию игры от сервера.</summary>
    public static async Task<(ResolvedProfile Profile, IReadOnlyList<LocalMod> Mods)> ScanAsync(ModTarget target, CancellationToken ct = default)
    {
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
        };
        var disabled = new HashSet<string>(list.DisabledMods);
        var mods = list.Mods.Select(m => new LocalMod(m.Path, m.Info, m.Error))
            .Select(l => l with { IsDisabled = ModUpdateService.IsDisabled(l, disabled) })
            .ToList();
        return (resolved, mods);
    }

    /// <summary>
    /// Поставить архив в цель: прежняя версия — в хранилище для отката, выключенный мод остаётся выключенным.
    /// <paramref name="confirm"/> видит план (та же версия, даунгрейд) и может отказаться — тогда null, ничего не изменено.
    /// </summary>
    public static async Task<ModInstallOutcome?> InstallAsync(ModTarget target, string zip, Func<InstallPlan, bool>? confirm = null,
        CancellationToken ct = default)
    {
        var (resolved, mods) = await ScanAsync(target, ct).ConfigureAwait(false);
        var plan = ModInstaller.Plan(zip, resolved, mods);
        if (confirm is not null && !confirm(plan)) return null;

        if (target.Remote is { } code)
        {
            // удалённому серверу план нужен только для вопросов: ставит его агент, по тем же правилам
            using var agent = AgentClient.ForRemote(code);
            var result = await agent.InstallModAsync(zip, ct).ConfigureAwait(false);
            return new ModInstallOutcome(result.Name, result.Version, result.OldVersion);
        }

        var info = plan.Incoming.Info!;
        var disabled = new HashSet<string>(resolved.DisabledMods);
        var wasDisabled = plan.Replaces.Any(r => ModUpdateService.IsDisabled(r, disabled));
        ModInstaller.Apply(plan, ModBackupStore.ForProfile(target.Profile));
        if (wasDisabled) ModConfigEditor.SetEnabled(ProfileResolver.Resolve(target.Profile), info, enabled: false);
        return new ModInstallOutcome(info.Name, info.Version, plan.Replaces.FirstOrDefault()?.Info?.Version);
    }

    /// <summary>Включить или выключить мод цели — так же, как это делает менеджер модов игры.</summary>
    public static async Task SetEnabledAsync(ModTarget target, LocalMod mod, bool enabled, CancellationToken ct = default)
    {
        if (target.Remote is { } code)
        {
            using var agent = AgentClient.ForRemote(code);
            await agent.SetModEnabledAsync(mod.Path, enabled, ct).ConfigureAwait(false);
            return;
        }
        ModConfigEditor.SetEnabled(ProfileResolver.Resolve(target.Profile), mod.Info ?? throw new InvalidOperationException(mod.Error ?? mod.Path), enabled);
    }
}

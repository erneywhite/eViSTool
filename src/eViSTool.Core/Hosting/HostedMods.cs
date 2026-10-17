using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.Core.Hosting;

/// <summary>
/// Операции с модами сервера у хостера — то же, что <see cref="ModTargets"/> делает со своим профилем, но по FTP:
/// скан (листинг + кэш modinfo), установка через временный файл, вкл/выкл в serverconfig.json, удаление с копией
/// на этой машине, отпечаток для опроса изменений со стороны.
/// </summary>
public sealed class HostedMods(ModTarget target)
{
    public ModTarget Target { get; } = target.IsHosted ? target : throw new ArgumentException("not a hosted target", nameof(target));

    public Task<(ResolvedProfile Profile, IReadOnlyList<LocalMod> Mods)> ScanAsync(IProgress<string>? progress, CancellationToken ct) =>
        throw new NotImplementedException("HostedMods.ScanAsync — пакет P3");

    public Task<ModInstallOutcome?> InstallAsync(string zip, Func<InstallPlan, bool>? confirm, CancellationToken ct) =>
        throw new NotImplementedException("HostedMods.InstallAsync — пакет P3");

    public Task SetEnabledAsync(string path, bool enabled, CancellationToken ct) =>
        throw new NotImplementedException("HostedMods.SetEnabledAsync — пакет P3");

    public Task DeleteAsync(string path, CancellationToken ct) =>
        throw new NotImplementedException("HostedMods.DeleteAsync — пакет P3");

    public Task<string> StampAsync(CancellationToken ct) =>
        throw new NotImplementedException("HostedMods.StampAsync — пакет P3");
}

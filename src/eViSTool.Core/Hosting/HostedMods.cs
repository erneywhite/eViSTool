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
        NotReady<(ResolvedProfile, IReadOnlyList<LocalMod>)>("HostedMods.ScanAsync");

    public Task<ModInstallOutcome?> InstallAsync(string zip, Func<InstallPlan, bool>? confirm, CancellationToken ct) =>
        NotReady<ModInstallOutcome?>("HostedMods.InstallAsync");

    public Task SetEnabledAsync(string path, bool enabled, CancellationToken ct) =>
        NotReady<bool>("HostedMods.SetEnabledAsync");

    public Task DeleteAsync(string path, CancellationToken ct) =>
        NotReady<bool>("HostedMods.DeleteAsync");

    public Task<string> StampAsync(CancellationToken ct) =>
        NotReady<string>("HostedMods.StampAsync");

    // shortcut: заглушки до пакета P3, там и заменить настоящими операциями. Ошибка — RemoteFilesException (наследник
    // IOException), а не NotImplementedException: её ловят фильтры окна, и промежуточная сборка не падает,
    // если профиль у хостера попал, например, в «Поставить заодно»
    private static Task<T> NotReady<T>(string what) =>
        Task.FromException<T>(new RemoteFilesException(RemoteFailure.Unsupported, what + " is not implemented yet"));
}

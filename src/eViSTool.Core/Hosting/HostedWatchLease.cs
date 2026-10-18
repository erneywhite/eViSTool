using eViSTool.Core.Server;

namespace eViSTool.Core.Hosting;

/// <summary>
/// Какое окно eViSTool на этой машине следит за сервером у хостера ради оповещений. Окон может быть открыто несколько,
/// а оповещение о событии должно прийти одно. Замок — монопольно открытый файл «&lt;профиль&gt;.hosted-watch.lock»
/// рядом с настройками оповещений, как у агента на Linux (<see cref="ProfileLock"/>): его видят все процессы машины,
/// а окно закрылось или упало — замок снимает сама система, и следить начинает другое окно.
/// </summary>
public sealed class HostedWatchLease : IDisposable
{
    private readonly FileStream _file;

    private HostedWatchLease(FileStream file) => _file = file;

    public static string FileFor(string profileId, string? agentsDir = null) =>
        Path.Combine(agentsDir ?? AgentProtocol.DefaultAgentsDir, $"{profileId}.hosted-watch.lock");

    /// <summary>Занять профиль. Уже следит другое окно (или файл не открыть) — null: попробовать позже.</summary>
    public static HostedWatchLease? TryTake(string profileId, string? agentsDir = null)
    {
        try
        {
            var path = FileFor(profileId, agentsDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new HostedWatchLease(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose() => _file.Dispose();
}

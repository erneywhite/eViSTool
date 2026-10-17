using System.IO.Compression;
using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Tests.Hosting;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// Сервер у хостера для теста: поддельный FTP над временной папкой (<see cref="FolderRemoteFiles"/>) на своём адресе,
/// профиль с подключением к нему и уборка всего, что операции пишут на этой машине (кэш, журнал, копии, история).
/// </summary>
internal sealed class HostedFixture : IDisposable
{
    private readonly IDisposable _registration;

    public string Dir { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-hosted-" + Guid.NewGuid().ToString("N"))).FullName;
    public string Host { get; } = "p3-" + Guid.NewGuid().ToString("N")[..12] + ".test";
    public FolderRemoteFiles Server { get; }
    public GameProfile Profile { get; }

    public HostedFixture(bool gportal = true, string root = "/")
    {
        var serverDir = Path.Combine(Dir, "server");
        Server = gportal ? FolderRemoteFiles.GPortal(serverDir) : new FolderRemoteFiles(serverDir);
        _registration = Server.Register(Host);
        Profile = new GameProfile
        {
            Id = "p3-" + Guid.NewGuid().ToString("N"),
            Name = "Hosted",
            Kind = ProfileKind.Server,
            RemoteCode = GameProfile.HostedMarker,
            Hosted = new HostedConnection { Host = Host, Port = 21, User = "user", Root = root },
        };
    }

    public HostedConnection Settings => Profile.Hosted!;

    /// <summary>Цель — снимок профиля на этот момент, как у окна.</summary>
    public ModTarget Target => ModTarget.For(Profile) ?? throw new InvalidOperationException("no target");

    public HostedEndpoint Endpoint => HostedEndpoint.From(Settings)!;

    /// <summary>Архив мода (на этой машине — как скачанный с модбазы).</summary>
    public string Zip(string fileName, string modId, string version, string? name = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Dir, "dl", Guid.NewGuid().ToString("N")[..6])).FullName;
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, ZipBytes(modId, version, name));
        return path;
    }

    public static byte[] ZipBytes(string modId, string version, string? name = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open()))
                w.Write($$"""{ "type": "code", "modid": "{{modId}}", "name": "{{name ?? modId}}", "version": "{{version}}" }""");
            using (var w = new StreamWriter(zip.CreateEntry("assets/readme.txt").Open()))
                w.Write("payload " + modId + " " + version);
        }
        return ms.ToArray();
    }

    /// <summary>Мод на «сервере».</summary>
    public void PutMod(string remotePath, string modId, string version) => Server.Put(remotePath, ZipBytes(modId, version));

    /// <summary>
    /// Раскладка как у G-Portal: корень — и игра, и данные; /Mods с базовыми dll, pdb и txt; gportal_mods с модом
    /// хостера; serverconfig.json после первого запуска; начало лога со списком папок модов.
    /// </summary>
    public void GPortalLayout(bool config = true, bool log = true, bool versionFile = true)
    {
        Server.Put("/VintagestoryServer.dll", [1, 2, 3]);
        Server.Put("/VintagestoryAPI.dll", [1, 2, 3]);
        if (versionFile) Server.Put("/version.txt", "linux-x64_1.22.7");
        foreach (var name in new[] { "VSSurvivalMod", "VSCreativeMod", "VSEssentials" })
        {
            Server.Put($"/Mods/{name}.dll", [1, 2, 3]);
            Server.Put($"/Mods/{name}.pdb", [1, 2, 3]);
        }
        Server.Put("/Mods/do_not_add_mods_here.txt", "do not add mods here");
        Server.Put("/gportal_mods/GPortalPlayerCount.cs", "// the host's own mod");
        if (config) Server.Put("/serverconfig.json", File.ReadAllBytes(Sample("gportal-serverconfig-after-start.json")));
        if (log) Server.Put("/Logs/server-main.log", GPortalLog);
    }

    public static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    /// <summary>Начало настоящего лога сервера G-Portal (номер сервера заменён).</summary>
    public const string GPortalLog = """
        17.10.2026 10:32:29 [Notification] Server logger started.
        17.10.2026 10:32:29 [Notification] Game Version: v1.22.7 (Stable)
        17.10.2026 10:32:30 [Notification] C# Framework: .net 10.0.10
        17.10.2026 10:32:30 [Notification] Savegame //home//sid_1234567//vintageStory//Saves//MyVintageStoryWorld.vcdbs loaded
        17.10.2026 10:32:30 [Notification] Entering runphase LoadAssets
        17.10.2026 10:32:30 [Notification] Will search the following paths for mods:
        17.10.2026 10:32:30 [Notification]     ~/vintageStory/Mods
        17.10.2026 10:32:30 [Notification]     ~/vintageStory/gportal_mods
        17.10.2026 10:32:33 [Notification] [GPortalPlayerCount.cs] Successfully compiled 1 source files
        17.10.2026 10:32:33 [Notification] Found 7 mods (0 disabled)
        17.10.2026 10:32:33 [Notification] Mods, sorted by dependency: bettersticks, gportalplayercount, game, absoluteprospecting, egocaribautomapmarkers, creative, survival

        """;

    public void Dispose()
    {
        _registration.Dispose();
        HostedSessions.Forget(Profile.Id);
        foreach (var dir in new[] { Dir, ModInfoCache.DirFor(Profile.Id), HostedJournal.DirFor(Profile.Id), Path.Combine(AppPaths.ModBackups, Profile.Id),
                     Path.Combine(AppPaths.ModConfigBackups, Profile.Id) })
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // временная папка — не страшно
            }
        if (File.Exists(ModHistory.FileFor(Profile.Id))) File.Delete(ModHistory.FileFor(Profile.Id));
    }
}

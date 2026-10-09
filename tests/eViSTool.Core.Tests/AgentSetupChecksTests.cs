using System.Runtime.InteropServices;
using eViSTool.Core.Platform;
using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

/// <summary>
/// Проверки команды setup на временных папках: читается ли игра, пишется ли в данные и в папку настроек, есть ли .NET,
/// от того ли пользователя запущено. Права Unix — только на Linux (от root — свои проверки, без root — свои).
/// </summary>
public sealed class AgentSetupChecksTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "evistool-setupcheck-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
            foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Prepend(_root))
                try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch (IOException) { }
        Directory.Delete(_root, recursive: true);
    }

    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private string Game(string name)
    {
        var dir = Dir(name);
        File.WriteAllText(Path.Combine(dir, "VintagestoryServer.dll"), "");
        if (OperatingSystem.IsWindows()) File.WriteAllText(Path.Combine(dir, "VintagestoryServer.exe"), "");
        return dir;
    }

    [Fact]
    public void Game_Readable_IsOk_WithoutServerFiles_IsAProblem()
    {
        Assert.Equal(SetupLevel.Ok, AgentSetup.CheckGame(Game("server")).Level);
        var empty = AgentSetup.CheckGame(Dir("empty"));
        Assert.Equal(SetupLevel.Problem, empty.Level);
        Assert.Contains("VintagestoryServer.dll", empty.Text);
    }

    [Fact]
    public void Data_Writable_IsOk_Missing_IsAWarning_ReadOnly_IsAProblem()
    {
        var data = Dir("data");
        File.WriteAllText(Path.Combine(data, "serverconfig.json"), "{}");
        Assert.Equal(SetupLevel.Ok, AgentSetup.CheckData(data).Level);
        Assert.Empty(Directory.GetFiles(data, ".evistool-setup-*")); // пробный файл убран

        // папки ещё нет: сервер её создаст — но и опечатку в пути видно
        var missing = AgentSetup.CheckData(Path.Combine(data, "new", "world"));
        Assert.Equal(SetupLevel.Warning, missing.Level);
        Assert.Contains(Path.Combine(data, "new", "world"), missing.Text);
        Assert.False(Directory.Exists(Path.Combine(data, "new")));

        var locked = Dir("locked");
        if (!DenyWrites(locked)) return; // root пишет куда угодно — тут проверять нечего
        try
        {
            var denied = AgentSetup.CheckData(locked);
            Assert.Equal(SetupLevel.Problem, denied.Level);
            Assert.Contains(locked, denied.Text); // чем поправить — с этой папкой
            Assert.Equal(SetupLevel.Problem, AgentSetup.CheckSettingsDir(locked).Level);
        }
        finally
        {
            AllowWrites(locked);
        }
    }

    [Fact]
    public void SettingsDir_IsCreated_OnlyForTheOwner()
    {
        var settings = Path.Combine(_root, "evistool", "data");
        Assert.Equal(SetupLevel.Ok, AgentSetup.CheckSettingsDir(settings).Level);
        Assert.True(Directory.Exists(settings));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(settings));
    }

    [Fact]
    public void Dotnet_ForTheServer_IsLookedForOnLinux()
    {
        var game = Path.Combine(_root, "server");
        FakeServer.InstallDllAs(game);
        if (OperatingSystem.IsWindows())
        {
            Assert.Null(AgentSetup.CheckDotnet(game)); // на Windows сервер — exe, .NET ему ставит игра
            return;
        }
        Assert.Equal(SetupLevel.Ok, AgentSetup.CheckDotnet(game)!.Level); // тестам он нужен и сам
        File.WriteAllText(Path.Combine(game, "VintagestoryServer.runtimeconfig.json"),
            """{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "99.0.0" } } }""");
        Assert.Equal(SetupLevel.Problem, AgentSetup.CheckDotnet(game)!.Level);
    }

    [Fact]
    public void NoServerFromThisGame_NothingToWarnAbout() =>
        Assert.Empty(AgentSetup.OtherServers(Game("server"), Dir("data"), ownServerPid: null));

    [Fact]
    public void Unix_OwnerAndName_OfAFolder()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Dir("mine");
        Assert.Equal(UnixAccount.CurrentUid, UnixAccount.OwnerOf(dir));
        Assert.Equal(Environment.UserName, UnixAccount.NameOf(UnixAccount.CurrentUid));
        Assert.Equal("root", UnixAccount.NameOf(0));
        Assert.Null(UnixAccount.OwnerOf(Path.Combine(dir, "nope")));
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead, UnixAccount.Stat(dir)!.Value.Mode);
    }

    [Fact]
    public void Unix_AsRoot_DataOfAnotherUser_SaysWhomToRunAs()
    {
        if (OperatingSystem.IsWindows()) return;
        var (data, app) = (Dir("data"), Dir("evistool"));
        if (!UnixAccount.IsRoot)
        {
            Assert.Null(AgentSetup.OwnerToRunAs(data, app)); // не root — запускать можно
            return;
        }
        Assert.Null(AgentSetup.OwnerToRunAs(data, app)); // всё и так у root
        Assert.Equal(0, chown(data, Nobody, Nobody));
        Assert.Equal(UnixAccount.Describe(Nobody), AgentSetup.OwnerToRunAs(data, app));
        // данных ещё нет — смотрим, чья папка над ними
        Assert.Equal(UnixAccount.Describe(Nobody), AgentSetup.OwnerToRunAs(Path.Combine(data, "new"), app));
        // данные у root, а папка eViSTool — нет: её файлы служба тоже не сможет менять
        Assert.Equal(0, chown(data, 0, 0));
        Assert.Equal(0, chown(app, Nobody, Nobody));
        Assert.Equal(UnixAccount.Describe(Nobody), AgentSetup.OwnerToRunAs(data, app));
    }

    [Fact]
    public void Unix_DataWithFilesThisUserCannotChange_IsAWarning()
    {
        if (OperatingSystem.IsWindows()) return;
        var data = Dir("data");
        File.WriteAllText(Path.Combine(data, "serverconfig.json"), "{}");
        Assert.Null(AgentSetup.CheckDataOwners(data)); // всё своё
        // чужой файл: ссылка на /etc/passwd — его владелец root, писать в него может только он
        File.CreateSymbolicLink(Path.Combine(Dir(Path.Combine("data", "Saves")), "default.vcdbs"), "/etc/passwd");
        var check = AgentSetup.CheckDataOwners(data);
        if (UnixAccount.IsRoot)
        {
            Assert.Null(check); // root может всё, а от root setup и так не пойдёт без --force
            return;
        }
        Assert.Equal(SetupLevel.Warning, check!.Level);
        Assert.Contains(Path.Combine(data, "Saves", "default.vcdbs"), check.Text);
        Assert.Contains($"sudo chown -R {Environment.UserName}: {data}", check.Text);
    }

    [Theory]
    [InlineData("/var/vintagestory/data", "/var/vintagestory/data")]
    [InlineData("--data", "--data")]
    [InlineData("/srv/my world", "'/srv/my world'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("", "''")]
    public void Shell_QuotesOnlyWhatNeedsIt(string arg, string quoted) => Assert.Equal(quoted, Shell.Quote(arg));

    private const uint Nobody = 65534;

    [DllImport("libc", SetLastError = true)]
    private static extern int chown(string path, uint owner, uint group);

    /// <summary>Запретить себе запись в папку; false — нельзя (root на Linux пишет куда угодно).</summary>
    private static bool DenyWrites(string dir)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (UnixAccount.IsRoot) return false;
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            return true;
        }
        var info = new DirectoryInfo(dir);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(WriteRule());
        info.SetAccessControl(acl);
        return true;
    }

    private static void AllowWrites(string dir)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }
        var info = new DirectoryInfo(dir);
        var acl = info.GetAccessControl();
        acl.RemoveAccessRule(WriteRule());
        info.SetAccessControl(acl);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static System.Security.AccessControl.FileSystemAccessRule WriteRule() => new(
        System.Security.Principal.WindowsIdentity.GetCurrent().User!,
        System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.CreateDirectories,
        System.Security.AccessControl.AccessControlType.Deny);
}

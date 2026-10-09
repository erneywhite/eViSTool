using eViSTool.Core.Game;

namespace eViSTool.Core.Tests;

/// <summary>Файл сервера и способ его запуска: на Windows — exe, на Linux — dotnet + dll, как у server.sh.</summary>
public sealed class ServerExecutableTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-srvexe-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void ServerFile_DependsOnThePlatform()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? "VintagestoryServer.exe" : "VintagestoryServer.dll", ServerExecutable.FileName);
        Assert.Equal(Path.Combine("game", ServerExecutable.FileName), ServerExecutable.PathIn("game"));
    }

    [Fact]
    public void Dll_IsStartedThroughDotnet_FromTheServerFolder()
    {
        var game = Path.Combine(_root, "game");
        var dll = FakeServer.InstallDllAs(game);

        var psi = ServerExecutable.StartInfo(dll);
        Assert.Equal(ServerExecutable.FindDotnet(), psi.FileName);
        Assert.Equal([dll], psi.ArgumentList);
        Assert.Equal(Path.GetDirectoryName(dll), psi.WorkingDirectory);
        Assert.False(psi.UseShellExecute);

        // exe (Windows) — напрямую, как раньше
        if (OperatingSystem.IsWindows())
        {
            var server = FakeServer.InstallAs(game);
            psi = ServerExecutable.StartInfo(server);
            Assert.Equal(server, psi.FileName);
            Assert.Empty(psi.ArgumentList);
        }
        else
        {
            // запускатель Linux без расширения — всё равно dll через dotnet: он искал бы .NET по своим правилам
            File.Copy(Path.Combine(FakeServer.Bin, "FakeVsServer"), Path.ChangeExtension(dll, null), overwrite: true);
            psi = ServerExecutable.StartInfo(Path.ChangeExtension(dll, null));
            Assert.Equal(ServerExecutable.FindDotnet(), psi.FileName);
            Assert.Equal([dll], psi.ArgumentList);
        }
    }

    [Fact]
    public void BrokenDll_IsRefusedBeforeStart()
    {
        var dll = Path.Combine(Directory.CreateDirectory(_root).FullName, ServerExecutable.DllName);
        File.WriteAllText(dll, "это не программа");
        var ex = Assert.Throws<InvalidOperationException>(() => ServerExecutable.StartInfo(dll));
        Assert.Contains(dll, ex.Message);
    }

    [Fact]
    public void RequiredRuntime_IsReadFromRuntimeConfig()
    {
        var dll = Path.Combine(Directory.CreateDirectory(_root).FullName, ServerExecutable.DllName);
        Assert.Null(ServerExecutable.RequiredRuntime(dll)); // runtimeconfig нет — пусть решает dotnet

        // как у сервера 1.22.7
        File.WriteAllText(Path.ChangeExtension(dll, ".runtimeconfig.json"), """
            { "runtimeOptions": { "tfm": "net10.0", "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" } } }
            """);
        Assert.Equal(new ServerExecutable.Runtime("Microsoft.NETCore.App", new Version(10, 0, 0), false), ServerExecutable.RequiredRuntime(dll));

        File.WriteAllText(Path.ChangeExtension(dll, ".runtimeconfig.json"), """
            { "runtimeOptions": { "rollForward": "LatestMajor", "frameworks": [
              { "name": "Microsoft.WindowsDesktop.App", "version": "8.0.0" }, { "name": "Microsoft.NETCore.App", "version": "8.0.1" } ] } }
            """);
        Assert.Equal(new ServerExecutable.Runtime("Microsoft.NETCore.App", new Version(8, 0, 1), true), ServerExecutable.RequiredRuntime(dll));
    }

    [Fact]
    public void Runtime_IsLookedUpNextToTheRealDotnet()
    {
        // своя «установка dotnet»: только среда 10.0.12, как в пакете Ubuntu
        var dotnetDir = Directory.CreateDirectory(Path.Combine(_root, "dotnet")).FullName;
        var dotnet = Path.Combine(dotnetDir, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        File.WriteAllText(dotnet, "");
        Directory.CreateDirectory(Path.Combine(dotnetDir, "shared", "Microsoft.NETCore.App", "10.0.12"));

        Assert.True(ServerExecutable.HasRuntime(dotnet, new("Microsoft.NETCore.App", new Version(10, 0, 0), false)));
        Assert.False(ServerExecutable.HasRuntime(dotnet, new("Microsoft.NETCore.App", new Version(11, 0, 0), false)));
        Assert.False(ServerExecutable.HasRuntime(dotnet, new("Microsoft.NETCore.App", new Version(9, 0, 0), false))); // другая старшая
        Assert.True(ServerExecutable.HasRuntime(dotnet, new("Microsoft.NETCore.App", new Version(9, 0, 0), true)));   // если разрешено
        Assert.False(ServerExecutable.HasRuntime(dotnet, new("Microsoft.AspNetCore.App", new Version(10, 0, 0), false)));

        if (OperatingSystem.IsWindows()) return;
        // /usr/bin/dotnet — ссылка на настоящий файл, среды лежат рядом с ним, а не рядом со ссылкой
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        File.CreateSymbolicLink(Path.Combine(bin, "dotnet"), dotnet);
        Assert.True(ServerExecutable.HasRuntime(Path.Combine(bin, "dotnet"), new("Microsoft.NETCore.App", new Version(10, 0, 0), false)));
    }

    [Fact]
    public void WithoutTheNeededDotnet_TheErrorSaysWhatToInstall()
    {
        var dll = FakeServer.InstallDllAs(Path.Combine(_root, "game"));
        // будто сервер новой версии хочет .NET, которого нет ни у кого
        File.WriteAllText(Path.ChangeExtension(dll, ".runtimeconfig.json"), """
            { "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "99.0.0" } } }
            """);
        var ex = Assert.Throws<InvalidOperationException>(() => ServerExecutable.StartInfo(dll));
        Assert.Contains("99", ex.Message);
        Assert.Contains("DOTNET_ROOT", ex.Message);
    }

    [Fact]
    public void GameVersion_IsReadFromTheServerFileOfThisPlatform()
    {
        // на Linux exe нет — версию даёт dll; у поддельного сервера версии как у настоящего 1.22.7, и на Linux
        // ProductVersion там «1.22.7.0» — показывать надо так же, как на Windows
        var game = Path.Combine(_root, "game");
        FakeServer.InstallDllAs(game);
        foreach (var exe in Directory.GetFiles(game, "*.exe")) File.Delete(exe);
        Assert.Equal("1.22.7", GameInstall.DetectVersion(game)?.ToString());
    }
}

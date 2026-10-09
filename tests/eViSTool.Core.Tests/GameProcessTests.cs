using System.Collections.Concurrent;
using System.Diagnostics;
using eViSTool.Core.Game;
using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

/// <summary>Копии игры с разными папками данных различаются по --dataPath в командной строке процесса.</summary>
public sealed class GameProcessTests
{
    [Theory]
    [InlineData("\"S:\\Games\\Vintagestory\\Vintagestory.exe\" --dataPath \"C:\\Data\\Мой профиль\"", "C:\\Data\\Мой профиль")]
    [InlineData("Vintagestory.exe --dataPath C:\\Data\\solo --tracelog", "C:\\Data\\solo")]
    [InlineData("Vintagestory.exe --dataPath=C:\\Data\\eq", "C:\\Data\\eq")]
    [InlineData("Vintagestory.exe --DATAPATH \"C:\\x\"", "C:\\x")]
    [InlineData("Vintagestory.exe", null)]
    public void DataPath_IsReadFromTheCommandLine(string commandLine, string? expected) =>
        Assert.Equal(expected, ProcessCommandLine.Argument(commandLine, "--dataPath"));

    [Fact]
    public void SameDataPath_TreatsTheDefaultFolderAsNoArgument_AndIgnoresCaseAndSlash()
    {
        Assert.True(GameProcess.SameDataPath(null, null));
        Assert.True(GameProcess.SameDataPath(GameInstall.DefaultDataDir, null)); // явно указана стандартная — то же самое
        Assert.True(GameProcess.SameDataPath(@"C:\Data\Solo\", @"c:\data\solo"));
        Assert.False(GameProcess.SameDataPath(@"C:\Data\Solo", null));
        Assert.False(GameProcess.SameDataPath(@"C:\Data\Solo", @"C:\Data\SoloTwo"));
    }

    /// <summary>
    /// «Чужой» поддельный сервер: запущен напрямую, не через ServerHost, но тем же способом, что и настоящий (на Linux —
    /// dotnet + dll). Освобождение убивает его, если он ещё жив.
    /// </summary>
    private sealed class Foreign : IDisposable
    {
        public Process Process { get; }
        public ConcurrentQueue<string> Lines { get; } = new();
        private readonly TaskCompletionSource _running = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Foreign(string serverFile, string dataPath, params string[] extra)
        {
            var psi = ServerExecutable.StartInfo(serverFile);
            psi.ArgumentList.Add("--dataPath");
            psi.ArgumentList.Add(dataPath);
            foreach (var a in extra) psi.ArgumentList.Add(a);
            psi.RedirectStandardInput = true; // ввод держим открытым, как у сервера в консоли
            psi.RedirectStandardOutput = true;
            psi.CreateNoWindow = true;
            Process = Process.Start(psi)!;
            Process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not { } line) return;
                Lines.Enqueue(line);
                if (line.Contains("now running")) _running.TrySetResult();
            };
            Process.BeginOutputReadLine();
        }

        public Task Running => _running.Task.WaitAsync(TimeSpan.FromSeconds(30));

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited) Process.Kill(entireProcessTree: true);
                Process.WaitForExit(10000); // иначе на Windows его exe ещё занят, и папку игры не удалить
            }
            catch (InvalidOperationException) { }
            Process.Dispose();
        }
    }

    [Fact]
    public async Task CommandLine_OfARealProcess_IsRead()
    {
        // настоящий процесс с «папкой данных» в командной строке и аргументами, которые легко испортить при разборе
        var data = Path.Combine(Path.GetTempPath(), "evistool cmd " + Guid.NewGuid().ToString("N"), "Мой мир");
        string[] extra = ["--note", "a \"b\" c", "", "--slowstart", "0"];
        using var server = new Foreign(FakeServer.ServerFile, data, extra);
        await server.Running;

        var cmd = ProcessCommandLine.Get(server.Process.Id);
        Assert.NotNull(cmd);
        Assert.Contains("--dataPath", cmd);
        Assert.Equal(data, ProcessCommandLine.Argument(cmd!, "--dataPath"));

        // по отдельности — ровно как передали (на Linux из /proc/<pid>/cmdline, на Windows — разбором строки)
        var args = ProcessCommandLine.GetArgs(server.Process.Id);
        Assert.NotNull(args);
        Assert.Equal(["--dataPath", data, .. extra], args!.Skip(args.Count - extra.Length - 2));
    }

    [Fact]
    public void CommandLine_OfAMissingProcess_IsNull()
    {
        Assert.Null(ProcessCommandLine.Get(int.MaxValue - 7));
        Assert.Null(ProcessCommandLine.GetArgs(int.MaxValue - 7));
    }

    [Fact]
    public void ProcCmdline_IsSplitByZeros_KeepingEmptyArgumentsAndUtf8()
    {
        static byte[] Raw(string s) => System.Text.Encoding.UTF8.GetBytes(s);
        Assert.Equal(["dotnet", "VintagestoryServer.dll", "--dataPath", "/var/vintage story/мир", "", "x"],
            ProcessCommandLine.ParseProcCmdline(Raw("dotnet\0VintagestoryServer.dll\0--dataPath\0/var/vintage story/мир\0\0x\0")));
        Assert.Empty(ProcessCommandLine.ParseProcCmdline(Raw(""))); // поток ядра, зомби
        Assert.Equal(["sleep"], ProcessCommandLine.ParseProcCmdline(Raw("sleep"))); // без завершающего \0 — тоже
    }

    [Fact]
    public void DotnetCommandLine_IsRecognizedAsServer_OnlyWhenItRunsTheServerDll()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "vs server");
        var dll = Path.Combine(cwd, ServerExecutable.DllName);

        // как server.sh: из папки игры, путь к dll относительный
        var r = GameProcess.DotnetServer(["dotnet", "VintagestoryServer.dll", "--dataPath", "/var/vs/data"], cwd);
        Assert.NotNull(r);
        Assert.Equal(dll, r!.Value.ServerFile);
        Assert.Equal(["--dataPath", "/var/vs/data"], r.Value.Args);

        // путь полный, перед ним параметры самого dotnet
        r = GameProcess.DotnetServer(["/usr/lib/dotnet/dotnet", "exec", "--runtimeconfig", "x.json", dll, "--dataPath", "d"], null);
        Assert.Equal(dll, r!.Value.ServerFile);
        Assert.Equal(["--dataPath", "d"], r.Value.Args);

        // рабочая папка неизвестна (чужой пользователь) — это сервер, но откуда — не узнать
        r = GameProcess.DotnetServer(["dotnet", "VintagestoryServer.dll"], null);
        Assert.NotNull(r);
        Assert.Null(r!.Value.ServerFile);

        // другое приложение в dotnet, даже если сервер упомянут дальше в аргументах
        Assert.Null(GameProcess.DotnetServer(["dotnet", "exec", "testhost.dll", "VintagestoryServer.dll"], cwd));
        Assert.Null(GameProcess.DotnetServer(["dotnet", "build"], cwd));
        Assert.Null(GameProcess.DotnetServer(["dotnet"], cwd));
    }

    [Fact]
    public async Task ForeignServer_IsFound_WithItsDataPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "evistool-foreign-" + Guid.NewGuid().ToString("N"));
        try
        {
            // папка игры с сервером под настоящим именем; запущен не нами, папка данных — с пробелом и кириллицей
            var game = Path.Combine(root, "game");
            var serverFile = FakeServer.InstallAs(game);
            var data = Path.Combine(root, "мой мир");
            using var server = new Foreign(serverFile, data, "--slowstart", "0");
            await server.Running;

            var found = Assert.Single(GameProcess.FindServers(game), s => s.Pid == server.Process.Id);
            Assert.Equal(serverFile, found.ServerFile);
            Assert.Equal(data, found.DataPath);
            Assert.Contains(server.Process.Id, GameProcess.FindServerPids(new Profiles.GameProfile { Kind = Profiles.ProfileKind.Server, GameDir = game }));
            Assert.True(GameProcess.IsRunning(new Profiles.GameProfile { Kind = Profiles.ProfileKind.Server, GameDir = game }));

            // сервер из другой папки игры к этой не относится
            Assert.DoesNotContain(GameProcess.FindServers(Path.Combine(root, "другая игра")), s => s.Pid == server.Process.Id);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task SoftStop_StopsAForeignServer_LikeTheRealOneStops()
    {
        Assert.False(SoftStop.Foreign(0));  // ни группе, ни «всем процессам» — никогда
        Assert.False(SoftStop.Foreign(-1));
        // на Windows Ctrl+C в чужую консоль шлёт только процесс без своей (окно eViSTool) — у тестов консоль есть
        if (!OperatingSystem.IsLinux()) return;

        using var server = new Foreign(FakeServer.ServerFile, Path.GetTempPath(), "--slowstart", "0");
        await server.Running;
        Assert.True(SoftStop.Foreign(server.Process.Id));
        await server.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, server.Process.ExitCode); // сохранился и вышел сам, как настоящий на SIGTERM, а не убит (137)
        Assert.Contains(server.Lines, l => l.Contains("termination event SIGTERM"));
    }
}

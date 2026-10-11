using System.Diagnostics;
using System.Windows;
using eViSTool.App.ViewModels;
using eViSTool.Presentation;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;
using Newtonsoft.Json.Linq;

// вопросы пользователю подменяются глобально (Ui.Platform) — тесты этой сборки идут по одному
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace eViSTool.App.Tests;

/// <summary>Модели окна живут в потоке STA (таймеры, представления коллекций) — тело теста выполняется там.</summary>
internal static class Sta
{
    public static void Run(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            // как в окне: продолжения после await возвращаются в этот поток (через его диспетчер)
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            if (!Ui.IsSet) Ui.Platform = new WpfUiPlatform();
            if (!AppWindows.IsSet) AppWindows.Current = new WpfAppWindows();
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>
    /// Крутить очередь диспетчера, пока не выполнится условие: модель что-то делает в фоне (запрос к агенту) и
    /// продолжает в потоке окна. Не дождались за 20 секунд — тест падает.
    /// </summary>
    /// <summary>Дождаться команды модели: её продолжения идут через этот же поток, поэтому просто блокировать его нельзя.</summary>
    public static void Wait(Task task)
    {
        WaitFor(() => task.IsCompleted);
        task.GetAwaiter().GetResult(); // ошибка команды — в тест
    }

    public static void WaitFor(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!done())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Не дождались условия в потоке окна");
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }
}

/// <summary>Ответы «пользователя» на вопросы по порядку; что спрашивали и о чём предупреждали — для проверок.</summary>
internal sealed class ScriptedDialogs : IDisposable
{
    private readonly Queue<Answer> _answers;
    public List<string> Asked { get; } = [];
    public List<string> Warned { get; } = [];

    public ScriptedDialogs(params Answer[] answers)
    {
        _answers = new Queue<Answer>(answers);
        Ui.Platform = new ScriptedPlatform(this);
    }

    public void Dispose() => Ui.Platform = new ScriptedPlatform(null);

    internal Answer Next(string text)
    {
        Asked.Add(text);
        return _answers.Count > 0 ? _answers.Dequeue() : throw new InvalidOperationException("Неожиданный вопрос: " + text);
    }
}

/// <summary>Платформа с заранее заданными ответами; без сценария (null) любой вопрос или сообщение роняет тест.</summary>
internal sealed class ScriptedPlatform(ScriptedDialogs? script) : WpfUiPlatform
{
    public override Answer Ask(string text, AskButtons buttons, Answer? byDefault = null, bool warning = false) =>
        script is null ? throw new InvalidOperationException("Вопрос без сценария: " + text) : script.Next(text);

    public override void Notify(string text, NoticeKind kind)
    {
        if (script is null) throw new InvalidOperationException("Предупреждение без сценария: " + text);
        script.Warned.Add(text);
    }
}

/// <summary>Серверный профиль для тестов: свой (папка на диске) или удалённый (настоящий агент с поддельным сервером).</summary>
internal sealed record TestServer(string Name, string DataDir, GameProfile View, ConnectionCode? Code)
{
    public bool IsRemote => Code is not null;
    public string ConfigPath => Path.Combine(DataDir, ProfileResolver.ServerConfigName);
    public string ServerName => JObject.Parse(File.ReadAllText(ConfigPath))["ServerName"]!.ToString();
}

/// <summary>Четыре серверных профиля на весь класс тестов: два своих и два удалённых (агенты поднимаются один раз).</summary>
public sealed class TestServers : IAsyncLifetime
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string AgentExe = Path.Combine(Root, "src", "eViSTool.Agent", "bin", "Debug", "net10.0", "eViSTool.Agent.exe");
    private static readonly string FakeBin = Path.Combine(Root, "tests", "FakeVsServer", "bin", "Debug", "net10.0");
    private static readonly string Sample = Path.Combine(AppContext.BaseDirectory, "TestData", "serverconfig-real.json");

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "evistool-apptests-" + Guid.NewGuid().ToString("N"));
    private readonly List<AgentClient> _agents = [];
    private string AgentsDir => Path.Combine(_tmp, "agents");

    internal TestServer LocalA { get; private set; } = null!;
    internal TestServer LocalB { get; private set; } = null!;
    internal TestServer RemoteA { get; private set; } = null!;
    internal TestServer RemoteB { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var game = Directory.CreateDirectory(Path.Combine(_tmp, "game")).FullName;
        foreach (var f in Directory.GetFiles(FakeBin)) File.Copy(f, Path.Combine(game, Path.GetFileName(f)));
        File.Copy(Path.Combine(FakeBin, "FakeVsServer.exe"), Path.Combine(game, "VintagestoryServer.exe"));

        LocalA = Local("local-a", game);
        LocalB = Local("local-b", game);
        RemoteA = await RemoteAsync("remote-a", game);
        RemoteB = await RemoteAsync("remote-b", game);
        Reset();
    }

    private TestServer Local(string name, string game)
    {
        var data = Directory.CreateDirectory(Path.Combine(_tmp, name)).FullName;
        var id = name + "-" + Guid.NewGuid().ToString("N")[..6];
        return new TestServer(name, data, new GameProfile { Id = id, Name = name, Kind = ProfileKind.Server, GameDir = game, DataDir = data }, null);
    }

    private async Task<TestServer> RemoteAsync(string name, string game)
    {
        var server = Local(name, game);
        var profile = server.View;
        var remote = RemoteAccess.Enable(profile.Id, AgentsDir);
        string fingerprint;
        using (var cert = RemoteAccess.EnsureCertificate(profile.Id, AgentsDir)) fingerprint = RemoteAccess.Fingerprint(cert);
        var agent = await AgentLauncher.EnsureRunningAsync(profile, startServer: false, AgentExe, AgentsDir);
        _agents.Add(agent);
        var until = DateTime.Now.AddSeconds(15);
        while ((await agent.StatusAsync()).RemotePort != remote.Port)
        {
            if (DateTime.Now > until) throw new TimeoutException(name);
            await Task.Delay(100);
        }
        // окно знает удалённый профиль только по id (кэш копии конфига) — свой id, как у профиля на другой машине
        var view = new GameProfile { Id = "view-" + profile.Id, Name = name, Kind = ProfileKind.Server };
        return server with { View = view, Code = new ConnectionCode("127.0.0.1", remote.Port, remote.Key, fingerprint) };
    }

    /// <summary>Перед каждым тестом: у каждого сервера в конфиге своё имя, кэш окна пуст.</summary>
    internal void Reset()
    {
        foreach (var s in new[] { LocalA, LocalB, RemoteA, RemoteB })
        {
            var json = JObject.Parse(File.ReadAllText(Sample));
            json["ServerName"] = s.Name;
            File.WriteAllText(s.ConfigPath, json.ToString());
            var cache = Path.Combine(Core.AppPaths.Cache, "remote", s.View.Id);
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var agent in _agents)
        {
            try { await agent.ShutdownAsync(); } catch (Exception) { /* уже завершён */ }
            var pid = agent.Endpoint.Pid;
            agent.Dispose();
            try { using var p = Process.GetProcessById(pid); p.WaitForExit(10000); if (!p.HasExited) p.Kill(true); }
            catch (ArgumentException) { }
        }
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }
}

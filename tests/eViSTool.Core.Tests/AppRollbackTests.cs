using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using eViSTool.Core.AppUpdate;

namespace eViSTool.Core.Tests;

/// <summary>
/// Прежняя версия после самообновления: хранится до двух запусков новой и суток, одно поколение, откат на неё и папка,
/// куда нельзя писать. Папка программы — временная; архивы — с главной программой этой системы (по ней Install узнаёт
/// архив eViSTool), часы подменены.
/// </summary>
public sealed class AppRollbackTests : IDisposable
{
    private static readonly string Main = AppUpdater.MainExe;
    private const string Lib = "lib.dll";

    /// <summary>Полдень по UTC: первый запуск новой версии.</summary>
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-rollback-" + Guid.NewGuid().ToString("N"));
    private readonly string _app;

    public AppRollbackTests()
    {
        _app = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        Write(Main, "main 1.0.0");
        Write(Lib, "lib 1.0.0");
        Directory.CreateDirectory(Path.Combine(_app, "data"));
        Write(Path.Combine("data", "settings.json"), "my settings");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_app, name), text);
    private string Text(string name) => File.ReadAllText(Path.Combine(_app, name));
    private string[] Olds => Directory.GetFiles(_app, "*.old");
    private bool HasRecord => File.Exists(Path.Combine(_app, AppRollback.FileName));

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Архив версии <paramref name="version"/>: главная программа, lib.dll и <paramref name="extra"/>, в каждом — имя и версия.</summary>
    private string Zip(string version, params string[] extra)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in new[] { Main, Lib }.Concat(extra))
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write((name == Main ? "main" : name == Lib ? "lib" : name) + " " + version);
        }
        return path;
    }

    /// <summary>Обновление тем же путём, что у окна и агента: стояла <paramref name="from"/>, ставим <paramref name="to"/>.</summary>
    private void Update(string from, string to, params string[] extra) => AppUpdater.Install(Zip(to, extra), _app, from, to);

    [Fact]
    public void PreviousVersion_IsKept_UntilTwoStarts_AndADay()
    {
        Update("1.0.0", "2.0.0");
        Assert.Equal("main 2.0.0", Text(Main));
        Assert.Equal(2, Olds.Length);
        Assert.Equal("1.0.0", AppRollback.Available(_app, "2.0.0"));

        var clock = new Clock(T0);
        AppRollback.NoteStart(_app, AppProgram.Window, "2.0.0", clock);
        clock.Now = T0.AddHours(2);
        AppRollback.NoteStart(_app, AppProgram.Window, "2.0.0", clock); // два запуска есть, суток ещё нет
        Assert.Equal(2, Olds.Length);
        Assert.Equal("1.0.0", AppRollback.Available(_app, "2.0.0"));

        clock.Now = T0.AddHours(25);
        AppRollback.NoteStart(_app, AppProgram.Window, "2.0.0", clock);
        Assert.Empty(Olds);
        Assert.Null(AppRollback.Available(_app, "2.0.0"));
        Assert.Equal("main 2.0.0", Text(Main));
        Assert.Equal("my settings", Text(Path.Combine("data", "settings.json")));
    }

    // окно при запуске поднимает агентов: их запуски не должны сойти за второй запуск окна
    [Fact]
    public void WindowStarts_Count_NotTheAgentsItLaunches()
    {
        Update("1.0.0", "2.0.0");
        var clock = new Clock(T0);
        AppRollback.NoteStart(_app, AppProgram.Window, "2.0.0", clock);
        AppRollback.NoteStart(_app, AppProgram.Agent, "2.0.0", clock);
        AppRollback.NoteStart(_app, AppProgram.Agent, "2.0.0", clock);

        clock.Now = T0.AddDays(2);
        AppRollback.NoteStart(_app, AppProgram.Agent, "2.0.0", clock); // сутки прошли, а окно запускалось один раз
        Assert.Equal(2, Olds.Length);

        AppRollback.NoteStart(_app, AppProgram.Window, "2.0.0", clock);
        Assert.Empty(Olds);
    }

    // сервер без окна: считаются запуски агента
    [Fact]
    public void ServerWithoutWindow_CountsTheAgentStarts()
    {
        Update("1.0.0", "2.0.0");
        var clock = new Clock(T0);
        AppRollback.NoteStart(_app, AppProgram.Agent, "2.0.0", clock);
        Assert.Equal(2, Olds.Length);

        clock.Now = T0.AddHours(30);
        AppRollback.NoteStart(_app, AppProgram.Agent, "2.0.0", clock);
        Assert.Empty(Olds);
    }

    [Fact]
    public void TwoUpdatesInARow_KeepOnlyTheLastGeneration()
    {
        Update("1.0.0", "2.0.0");
        AppRollback.NoteStart(_app, AppProgram.Window, "2.0.0", new Clock(T0)); // 2.0.0 ещё не проработала

        // окно 1.0.0 могло не перезапуститься после первого обновления — в папке всё равно 2.0.0, это знает запись о нём
        Update("1.0.0", "3.0.0");
        Assert.Equal("main 3.0.0", Text(Main));
        Assert.Equal(2, Olds.Length);
        Assert.All(Olds, old => Assert.EndsWith(" 2.0.0", File.ReadAllText(old)));
        Assert.Equal("2.0.0", AppRollback.Available(_app, "3.0.0"));

        Assert.Equal("2.0.0", AppRollback.RollBack(_app, "3.0.0"));
        Assert.Equal("main 2.0.0", Text(Main));
        Assert.Equal("lib 2.0.0", Text(Lib));
    }

    [Fact]
    public void RollBack_SwapsTheFiles_AndTheNextStartCleansUp()
    {
        Update("1.0.0", "2.0.0", "new.txt"); // new.txt появился в 2.0.0

        Assert.Equal("1.0.0", AppRollback.RollBack(_app, "2.0.0"));
        Assert.Equal("main 1.0.0", Text(Main));
        Assert.Equal("lib 1.0.0", Text(Lib));
        Assert.False(File.Exists(Path.Combine(_app, "new.txt")));
        Assert.Equal("my settings", Text(Path.Combine("data", "settings.json")));
        Assert.Equal(3, Olds.Length); // снятые файлы 2.0.0 — обычные хвосты

        // поколение одно: обратно на 2.0.0 — уже обновлением
        Assert.Null(AppRollback.Available(_app, "1.0.0"));
        Assert.Throws<InvalidOperationException>(() => AppRollback.RollBack(_app, "1.0.0"));

        AppRollback.NoteStart(_app, AppProgram.Window, "1.0.0", new Clock(T0));
        Assert.Empty(Olds);

        // следующее обновление снова хранит прежнюю версию
        Update("1.0.0", "2.0.0");
        Assert.Equal("1.0.0", AppRollback.Available(_app, "2.0.0"));
    }

    [Fact]
    public void RollBack_FailingMidway_LeavesTheNewVersionWhole()
    {
        Write("zzz.txt", "zzz.txt 1.0.0");
        Update("1.0.0", "2.0.0", "zzz.txt");
        // zzz.txt откат ставит последним, а его место заняла папка: прежний файл туда не встанет
        File.Delete(Path.Combine(_app, "zzz.txt"));
        Directory.CreateDirectory(Path.Combine(_app, "zzz.txt"));

        var error = Record.Exception(() => AppRollback.RollBack(_app, "2.0.0"));

        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.Equal("main 2.0.0", Text(Main));
        Assert.Equal("lib 2.0.0", Text(Lib));
        Assert.Equal(3, Olds.Length);
        Assert.Equal("1.0.0", AppRollback.Available(_app, "2.0.0")); // прежняя версия цела — можно попробовать ещё раз
    }

    // обновление ставила версия до 0.14.1 (без записи) — как раньше: «*.old» убираются при первом же запуске
    [Fact]
    public void OldsWithoutRecord_AreRemovedAtTheFirstStart()
    {
        AppUpdater.Install(Zip("2.0.0"), _app);
        Assert.Equal(2, Olds.Length);
        Assert.Null(AppRollback.Available(_app, "2.0.0"));

        AppRollback.NoteStart(_app, AppProgram.Window, "2.0.0", new Clock(T0));
        Assert.Empty(Olds);
    }

    // файлы в папке поменяли мимо обновления: запись не про них — убирается вместе с «*.old»
    [Fact]
    public void RecordAboutOtherFiles_IsDropped()
    {
        Update("1.0.0", "2.0.0");

        AppRollback.NoteStart(_app, AppProgram.Window, "3.0.0", new Clock(T0));

        Assert.Empty(Olds);
        Assert.False(HasRecord);
        Assert.Null(AppRollback.Available(_app, "3.0.0"));
    }

    [Fact]
    public void ReadOnlyFolder_BehavesAsBefore()
    {
        if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess) return; // root пишет куда угодно
        Update("1.0.0", "2.0.0");

        using (new ReadOnlyDir(_app))
        {
            // запуск не падает, и вернуться не предлагается: в такой папке ни обновить, ни откатить
            AppRollback.NoteStart(_app, AppProgram.Window, "2.0.0", new Clock(T0));
            Assert.Null(AppRollback.Available(_app, "2.0.0"));
        }
        Assert.Equal("main 2.0.0", Text(Main));
    }

    /// <summary>Папка, в которой нельзя создавать файлы (на Windows — запрет в правах доступа, на Linux — r-x), пока объект жив.</summary>
    private sealed class ReadOnlyDir : IDisposable
    {
        private readonly string _dir;
        private readonly UnixFileMode _mode;

        public ReadOnlyDir(string dir)
        {
            _dir = dir;
            if (OperatingSystem.IsWindows())
            {
                DenyCreate(dir, deny: true);
            }
            else
            {
                _mode = File.GetUnixFileMode(dir);
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            }
        }

        public void Dispose()
        {
            if (OperatingSystem.IsWindows()) DenyCreate(_dir, deny: false);
            else File.SetUnixFileMode(_dir, _mode);
        }

        [SupportedOSPlatform("windows")]
        private static void DenyCreate(string dir, bool deny)
        {
            var info = new DirectoryInfo(dir);
            var acl = info.GetAccessControl();
            var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, AccessControlType.Deny);
            if (deny) acl.AddAccessRule(rule);
            else acl.RemoveAccessRule(rule);
            info.SetAccessControl(acl);
        }
    }
}

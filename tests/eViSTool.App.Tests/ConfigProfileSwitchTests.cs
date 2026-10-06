using System.Windows;
using eViSTool.App.ViewModels;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.App.Tests;

/// <summary>
/// Правки конфига при смене профиля (аудит, пункт 6): сохраняются туда, откуда конфиг загружен — свой файл или
/// сервер через его агента, — и никогда не теряются молча. Проверяется фактический файл на «сервере».
/// </summary>
public sealed class ConfigProfileSwitchTests : IClassFixture<TestServers>
{
    private readonly TestServers _s;

    public ConfigProfileSwitchTests(TestServers servers)
    {
        _s = servers;
        _s.Reset();
    }

    /// <summary>Редактор конфига и связь с агентом — в том же порядке, что у раздела «Сервер».</summary>
    private sealed class Editor : IDisposable
    {
        public ServerConfigViewModel Vm { get; } = new();
        public AgentClient? Client { get; set; }
        private TestServer _current = null!;

        public void Open(TestServer s)
        {
            _current = s;
            Client = s.Code is { } code ? AgentClient.ForRemote(code) : null;
            Show(s);
            Vm.SetActive(true);
        }

        private void Show(TestServer s)
        {
            Vm.OnProfileSwitched(s.IsRemote ? null : s.DataDir, s.Name, null, s.IsRemote ? s.View : null, () => Client);
            Vm.SetServerRunning(false);
        }

        /// <summary>Выбор в списке профилей: можно отменить. false — остались на прежнем.</summary>
        public bool Select(TestServer next)
        {
            if (!Vm.ConfirmSwitch()) return false;
            Replace(next);
            return true;
        }

        /// <summary>Необратимая смена (папку данных поменяли в настройках): только «да / нет».</summary>
        public void Replace(TestServer next)
        {
            Vm.BeforeSwitch(next.IsRemote ? null : next.DataDir, next.IsRemote ? next.View : null);
            Client?.Dispose(); // как раздел «Сервер»: связь со старым агентом закрывается после сохранения
            Client = next.Code is { } code ? AgentClient.ForRemote(code) : null;
            _current = next;
            Show(next);
        }

        public string ServerName
        {
            get => Field.Text;
            set => Field.Text = value;
        }

        private ConfigTextFieldViewModel Field =>
            Vm.GeneralGroups.SelectMany(g => g.Fields).OfType<ConfigTextFieldViewModel>().Single(f => f.Path == "ServerName");

        public void Dispose() => Client?.Dispose();
    }

    public static TheoryData<string, string> Transitions => new()
    {
        { "remote-a", "local-b" },
        { "remote-a", "remote-b" },
        { "local-a", "remote-b" },
        { "local-a", "local-b" },
    };

    private TestServer ByName(string name) => name switch
    {
        "local-a" => _s.LocalA,
        "local-b" => _s.LocalB,
        "remote-a" => _s.RemoteA,
        _ => _s.RemoteB,
    };

    [Theory]
    [MemberData(nameof(Transitions))]
    public void Save_GoesToTheProfileTheConfigCameFrom(string from, string to)
    {
        var (a, b) = (ByName(from), ByName(to));
        Sta.Run(() =>
        {
            using var dialogs = new ScriptedDialogs(MessageBoxResult.Yes);
            using var editor = new Editor();
            editor.Open(a);
            Assert.Equal(a.Name, editor.ServerName);
            editor.ServerName = "edited";
            Assert.True(editor.Vm.IsDirty);

            Assert.True(editor.Select(b));

            Assert.Single(dialogs.Asked);
            Assert.Empty(dialogs.Warned);
            Assert.Equal(b.Name, editor.ServerName); // открыт уже конфиг B
            Assert.False(editor.Vm.IsDirty);
        });
        Assert.Equal("edited", a.ServerName); // сохранено в A — на самом сервере
        Assert.Equal(b.Name, b.ServerName);   // B не тронут
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public void DontSave_DiscardsTheEdits_AndSwitches(string from, string to)
    {
        var (a, b) = (ByName(from), ByName(to));
        Sta.Run(() =>
        {
            using var dialogs = new ScriptedDialogs(MessageBoxResult.No);
            using var editor = new Editor();
            editor.Open(a);
            editor.ServerName = "edited";

            Assert.True(editor.Select(b));

            Assert.Single(dialogs.Asked); // спросили один раз, не дважды
            Assert.Equal(b.Name, editor.ServerName);
        });
        Assert.Equal(a.Name, a.ServerName);
        Assert.Equal(b.Name, b.ServerName);
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public void Cancel_StaysOnTheProfile_WithTheEdits(string from, string to)
    {
        var (a, b) = (ByName(from), ByName(to));
        Sta.Run(() =>
        {
            using var dialogs = new ScriptedDialogs(MessageBoxResult.Cancel);
            using var editor = new Editor();
            editor.Open(a);
            editor.ServerName = "edited";

            Assert.False(editor.Select(b));

            Assert.Equal("edited", editor.ServerName); // правки на месте
            Assert.True(editor.Vm.IsDirty);
        });
        Assert.Equal(a.Name, a.ServerName);
        Assert.Equal(b.Name, b.ServerName);
    }

    [Fact]
    public void FailedSave_OnSelect_StaysOnTheProfile_WithTheEdits()
    {
        var (a, b) = (_s.RemoteA, _s.LocalB);
        Sta.Run(() =>
        {
            using var dialogs = new ScriptedDialogs(MessageBoxResult.Yes);
            using var editor = new Editor();
            editor.Open(a);
            editor.ServerName = "edited";
            // связь с сервером A пропала (порт, где никого нет)
            editor.Client!.Dispose();
            editor.Client = AgentClient.ForRemote(a.Code! with { Port = 1 });

            Assert.False(editor.Select(b));

            Assert.Equal("edited", editor.ServerName);
            Assert.True(editor.Vm.IsDirty);
            Assert.Single(dialogs.Asked); // спросили один раз; не сохранилось — остаёмся, ничего не теряем
        });
        Assert.Equal(a.Name, a.ServerName);
        Assert.Equal(b.Name, b.ServerName);
    }

    [Fact]
    public void FailedSave_OnAnIrreversibleSwitch_KeepsTheEditsInADraft()
    {
        var (a, b) = (_s.RemoteA, _s.LocalB);
        var drafts = Path.Combine(Core.AppPaths.Root, "drafts");
        var before = Directory.Exists(drafts) ? Directory.GetFiles(drafts).ToHashSet() : [];
        List<string> warned = [];
        Sta.Run(() =>
        {
            using var dialogs = new ScriptedDialogs(MessageBoxResult.Yes);
            using var editor = new Editor();
            editor.Open(a);
            editor.ServerName = "edited";
            editor.Client!.Dispose();
            editor.Client = AgentClient.ForRemote(a.Code! with { Port = 1 });

            editor.Replace(b);

            Assert.Equal(b.Name, editor.ServerName);
            warned = dialogs.Warned;
        });
        var draft = Assert.Single(Directory.GetFiles(drafts), f => !before.Contains(f));
        try
        {
            Assert.Contains("\"edited\"", File.ReadAllText(draft));
            Assert.Contains(warned, w => w.Contains(draft)); // пользователю сказали, где черновик
            Assert.Equal(a.Name, a.ServerName);
        }
        finally
        {
            File.Delete(draft);
        }
    }
}

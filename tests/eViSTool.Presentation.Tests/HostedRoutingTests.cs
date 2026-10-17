using System.Reflection;
using System.Runtime.CompilerServices;
using eViSTool.Core;
using eViSTool.Core.Game;
using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Маршруты окна для профиля «Сервер у хостера»: раздел «Сервер» без монитора и вкладок, «Настройки модов» без
/// локального источника, «Играть» без хостовых профилей, клон и уборка диска выключены, создание и удаление профиля.
/// Главная модель «пустая» (без настроек и наблюдателей): ей задаётся только активный профиль.
/// </summary>
public sealed class HostedRoutingTests : IDisposable
{
    private readonly IAppWindows? _oldWindows = AppWindows.IsSet ? AppWindows.Current : null;
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        AppWindows.Current = _oldWindows!;
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static GameProfile HostedProfile() => new()
    {
        Id = "hr-" + Guid.NewGuid().ToString("N"),
        Name = "G-Portal",
        Kind = ProfileKind.Server,
        RemoteCode = GameProfile.HostedMarker,
        Hosted = new HostedConnection { Host = "198.51.100.7", Port = 58021, Tls = FtpTls.None, User = "gp_user", Password = HostedSecret.Protect("pw") },
    };

    private static ProfileViewModel Vm(GameProfile model) => new(model, _ => { });

    private static MainViewModel MainWith(ProfileViewModel? active)
    {
        var main = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        SetActive(main, active);
        return main;
    }

    /// <summary>Активный профиль в обход свойства: оно сохраняет настройки и будит все вкладки.</summary>
    private static void SetActive(MainViewModel main, ProfileViewModel? active) =>
        typeof(MainViewModel).GetField("_activeProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, active);

    private static T Done<T>(Task<T> task)
    {
        Sta.Wait(task);
        return task.Result;
    }

    // ---- раздел «Сервер»

    [Fact]
    public void ServerSection_HostedProfile_ShowsTheHostedPlateWithoutMonitor() => Sta.Run(() =>
    {
        var server = new ServerViewModel(MainWith(Vm(HostedProfile())));

        server.OnProfileSwitched();

        Assert.True(server.IsHostedProfile);
        Assert.False(server.IsServerProfile); // Kind = Server, но серверного раздела нет
        Assert.False(server.IsRemoteProfile);
        Assert.False(server.ShowNotServer); // у хостового профиля своя плашка, а не «это не сервер»
        Assert.False(server.AgentRunning);
        Assert.Equal(ServerState.Stopped, server.State);
        // вкладкам раздела профиль не передан: ни расписание, ни игроки, ни журнал не полезут к агенту и в папки
        foreach (var (name, tab) in new (string, object)[] { ("Schedule", server.Schedule), ("Players", server.Players),
                     ("Announcements", server.Announcements), ("Stats", server.Stats), ("Journal", server.Journal),
                     ("Notify", server.Notify), ("Remote", server.Remote) })
            Assert.True(TabProfile(tab) is null, name);
    });

    /// <summary>Профиль, который вкладка раздела «Сервер» получила последним (поле <c>_profile</c>).</summary>
    private static GameProfile? TabProfile(object tab) =>
        (GameProfile?)tab.GetType().GetField("_profile", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab);

    [Fact]
    public void ServerSection_ClientAndHosted_ThenBack_KeepFlagsInStep() => Sta.Run(() =>
    {
        var main = MainWith(Vm(new GameProfile { Id = "hr-client", Name = "Игра", Kind = ProfileKind.Client }));
        var server = new ServerViewModel(main);

        server.OnProfileSwitched();
        Assert.False(server.IsServerProfile);
        Assert.False(server.IsHostedProfile);
        Assert.True(server.ShowNotServer);

        SetActive(main, Vm(HostedProfile()));
        server.OnProfileSwitched();
        Assert.True(server.IsHostedProfile);
        Assert.False(server.ShowNotServer);

        SetActive(main, Vm(new GameProfile { Id = "hr-client2", Name = "Игра 2", Kind = ProfileKind.Client }));
        server.OnProfileSwitched();
        Assert.False(server.IsHostedProfile); // флаг не залипает
        Assert.True(server.ShowNotServer);
    });

    // монитор хостового профиля сеанса не начинает: агента у хостера нет, слушать нечего
    private sealed class Host(GameProfile profile) : IServerMonitorHost
    {
        public GameProfile? Profile { get; } = profile;
        public ServerState State => ServerState.Stopped;
        public bool IsBusy => false;
        public List<string> Calls { get; } = [];
        public void OnAttached(AgentClient client, CancellationToken session) => Calls.Add("attached");
        public void OnDetached() => Calls.Add("detached");
        public void OnStatus(AgentStatus status) => Calls.Add("status");
        public void OnLocalChecked(IReadOnlyList<int> serverPids, bool ourServerUp) => Calls.Add("local");
        public void OnRemoteAnswered() => Calls.Add("answered");
        public void OnRemoteChecked(Exception? error) => Calls.Add("remote");
        public void OnRemoteCodeUnreadable() => Calls.Add("unreadable");
    }

    [Fact]
    public void Monitor_HostedProfile_EndsAtOnce_AndReportsNothing() => Sta.Run(() =>
    {
        var host = new Host(HostedProfile());
        var monitor = new ServerMonitor(host);

        var session = monitor.Start();

        Assert.True(session.IsCompleted); // цикл не вошёл в ожидание
        Assert.Empty(host.Calls); // и не пытался разобрать метку как код подключения
    });

    // страховка: сейчас хостовый профиль отсеялся бы и без фильтра (IsRemote = false, файла агента нет — TryConnect
    // вернёт null), но фильтр не даст наблюдателю полезть к агенту, если метку когда-нибудь начнут читать иначе
    [Fact]
    public void CrashWatcher_HostedProfile_IsSkipped() => Sta.Run(() =>
    {
        var errors = new List<string>();
        var crashes = new List<string>();
        var watcher = new ServerCrashWatcher([Vm(HostedProfile())], (p, _) => errors.Add(p.Id), (p, c) => crashes.Add(p.Id));

        Sta.Wait(watcher.CheckAsync());

        Assert.Empty(errors);
        Assert.Empty(crashes);
    });

    // ---- «Настройки модов»

    [Fact]
    public void ModConfig_HostedProfile_GoesToTheServer_NotToTheLocalDisk() => Sta.Run(() =>
    {
        var model = new ModConfigViewModel(MainWith(Vm(HostedProfile())));

        Sta.Wait(model.LoadAsync());

        // источник — FTP сервера (транспорта FTP в тестах окна нет), а не папка данных игры этого компьютера
        Assert.Equal(Loc.T("mcfg.hostedFailed", Loc.T("hosted.errNoTransport")), model.Notice);
        Assert.Empty(model.Rows);
        Assert.False(model.CanShowFile); // локальной папки конфигов нет: «Показать файл» ничего не откроет
    });

    // ---- «Играть»

    // страховка: PlayTargets.Own уже не даёт цели для хостового профиля, фильтр меню держит это и без него
    [Fact]
    public void PlayMenu_HostedServers_AreNotListed() => Sta.Run(() =>
    {
        var hosted = Vm(HostedProfile());
        var own = Vm(new GameProfile { Id = "hr-own", Name = "Свой", Kind = ProfileKind.Server, DataDir = Path.Combine(Path.GetTempPath(), "hr-own") });
        var client = new GameProfile { Id = "hr-client", Name = "Игра", Kind = ProfileKind.Client };
        var items = new List<PlayMenuItemViewModel>();

        PlayMenu.FillOwnServers(items, [hosted, own], null, null);
        var options = PlayMenu.DefaultServerOptions(client, [hosted, own]);

        Assert.Equal(["Свой"], items.Select(i => i.Name));
        Assert.DoesNotContain(options, o => o.Title.Contains("G-Portal"));
        Assert.Null(PlayMenu.FindTarget([hosted], client, PlayTargets.OwnKey(hosted.Model.Id)));
    });

    // ---- клон и уборка диска

    [Fact]
    public void Clone_HostedProfile_DoesNothing() => Sta.Run(() =>
    {
        AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of(); // любое окно — ошибка теста
        var hosted = Vm(HostedProfile());
        var actions = new ProfileActions([hosted], () => hosted);

        Assert.Null(Done(actions.CloneAsync(hosted)));
    });

    [Fact]
    public void CleanupDisk_IsOnlyForLocalProfiles()
    {
        // предикат команды — приватный статический метод главной модели
        var can = typeof(MainViewModel).GetMethod("CanCleanupDisk", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool Can(GameProfile p) => (bool)can.Invoke(null, [Vm(p)])!;

        Assert.False(Can(HostedProfile()));
        Assert.False(Can(new GameProfile { Name = "Дальний", Kind = ProfileKind.Server, RemoteCode = "не-код" }));
        Assert.True(Can(new GameProfile { Name = "Свой", Kind = ProfileKind.Server, DataDir = Path.Combine(Path.GetTempPath(), "hr-x") }));
        Assert.False((bool)can.Invoke(null, [null])!);
    }

    // ---- создание и удаление профиля

    [Fact]
    public void NewServer_Hosted_MakesTheMarkedProfileWithTheConnection() => Sta.Run(() =>
    {
        var connection = HostedProfile().Hosted!;
        AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of((nameof(IAppWindows.NewProfile),
            _ => new NewProfileChoice("Мой хостер", "", ExistingProfileMode.Hosted, null, null, connection)));
        var actions = new ProfileActions([], () => null);

        var profile = actions.NewServer();

        Assert.NotNull(profile);
        Assert.Equal("Мой хостер", profile.Name);
        Assert.Equal(ProfileKind.Server, profile.Kind); // новый Kind старые окна не прочитали бы
        Assert.Equal(GameProfile.HostedMarker, profile.RemoteCode);
        Assert.Same(connection, profile.Hosted);
        Assert.True(profile.IsHosted);
        Assert.Null(profile.DataDir);
        Assert.Null(profile.GameDir);
    });

    [Fact]
    public void NewServer_HostedWithoutConnection_IsCancelled() => Sta.Run(() =>
    {
        AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of((nameof(IAppWindows.NewProfile),
            _ => new NewProfileChoice("Мой хостер", "", ExistingProfileMode.Hosted, null, null)));

        Assert.Null(new ProfileActions([], () => null).NewServer());
    });

    private string Dir(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "x.json"), "{}");
        _dirs.Add(path);
        return path;
    }

    [Fact]
    public void Remove_HostedProfile_ClearsCacheAndJournal_AfterAYes() => Sta.Run(() =>
    {
        var hosted = Vm(HostedProfile());
        var other = Vm(HostedProfile());
        var cache = Dir(ModInfoCache.DirFor(hosted.Model.Id));
        var journal = Dir(Path.Combine(AppPaths.Root, "hosted", hosted.Model.Id));
        var otherCache = Dir(ModInfoCache.DirFor(other.Model.Id));
        using var script = new ScriptedDialogs(Answer.Yes);
        var actions = new ProfileActions([hosted, other], () => null);

        Assert.True(Done(actions.PrepareRemoveAsync(hosted)));
        Assert.True(Directory.Exists(cache)); // пока профиль в списке, следы не трогаем: окно ещё может на нём стоять
        ProfileActions.AfterRemoved(hosted.Model);

        Assert.False(Directory.Exists(cache));
        Assert.False(Directory.Exists(journal));
        Assert.True(Directory.Exists(otherCache)); // чужой профиль не тронут
        Assert.Equal(Loc.T("settings.removeAskHosted", "G-Portal"), Assert.Single(script.Asked));
    });

    [Fact]
    public void Remove_HostedProfile_No_KeepsEverything() => Sta.Run(() =>
    {
        var hosted = Vm(HostedProfile());
        var cache = Dir(ModInfoCache.DirFor(hosted.Model.Id));
        using var script = new ScriptedDialogs(Answer.No);
        var actions = new ProfileActions([hosted], () => null);

        Assert.False(Done(actions.PrepareRemoveAsync(hosted)));

        Assert.True(Directory.Exists(cache));
    });

    [Fact]
    public void AfterRemoved_OwnProfile_LeavesHostedFoldersAlone() => Sta.Run(() =>
    {
        var own = new GameProfile { Id = "hr-" + Guid.NewGuid().ToString("N"), Name = "Свой", Kind = ProfileKind.Server };
        var cache = Dir(ModInfoCache.DirFor(own.Id));

        ProfileActions.AfterRemoved(own);

        Assert.True(Directory.Exists(cache));
    });

    // ---- правка подключения

    [Fact]
    public void EditConnection_SamePlaceNewPassword_KeepsCacheAndJournal() => Sta.Run(() =>
    {
        var hosted = Vm(HostedProfile());
        var cache = Dir(ModInfoCache.DirFor(hosted.Model.Id));
        var journal = Dir(Path.Combine(AppPaths.Root, "hosted", hosted.Model.Id));
        var edited = hosted.Model.Hosted!.Clone();
        edited.Password = HostedSecret.Protect("new");
        edited.Host = "198.51.100.7 ";
        edited.Root = "";
        edited.Tls = FtpTls.Auto;
        AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of((nameof(IAppWindows.AskHostedConnection), _ => edited));

        hosted.EditConnectionCommand.Execute(null);

        Assert.Same(edited, hosted.Model.Hosted);
        Assert.True(Directory.Exists(cache)); // сервер тот же: прочитанные modinfo годны
        Assert.True(Directory.Exists(journal)); // прерванную установку доведём на том же сервере
    });

    [Fact]
    public void EditConnection_OtherServerOrAccount_ClearsCacheAndJournal() => Sta.Run(() =>
    {
        foreach (var change in new Action<HostedConnection>[] { c => c.Host = "203.0.113.9", c => c.Port = 21, c => c.User = "other",
                     c => c.Root = "/g123456", c => c.Protocol = HostedProtocol.Sftp })
        {
            var hosted = Vm(HostedProfile());
            var cache = Dir(ModInfoCache.DirFor(hosted.Model.Id));
            var journal = Dir(Path.Combine(AppPaths.Root, "hosted", hosted.Model.Id));
            var edited = hosted.Model.Hosted!.Clone();
            change(edited);
            AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of((nameof(IAppWindows.AskHostedConnection), _ => edited));

            hosted.EditConnectionCommand.Execute(null);

            Assert.False(Directory.Exists(cache)); // чужие modinfo не подсовываем
            Assert.False(Directory.Exists(journal)); // журнал старого сервера на новом не доводим
        }
    });
}

using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server.Remote;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Добавление, клонирование и удаление профиля без главной модели: что спрашивается, какой профиль получается и что
/// происходит с папкой данных. Окна программы подменены; папки — во временной папке (в Корзину тестовая платформа
/// «убирает» удалением), данные пользователя не трогаются. Агентов у профилей нет — серверы считаются остановленными.
/// </summary>
public sealed class ProfileActionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-profileactions-" + Guid.NewGuid().ToString("N"));
    private readonly IAppWindows? _oldWindows = AppWindows.IsSet ? AppWindows.Current : null;

    public ProfileActionsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        AppWindows.Current = _oldWindows!; // null — не было: следующий тест поставит своё
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static ProfileViewModel Vm(GameProfile model) => new(model, _ => { });

    private ProfileViewModel ClientIn(string name) => Vm(new GameProfile
    {
        Id = "pa-" + Guid.NewGuid().ToString("N"),
        Name = name,
        Kind = ProfileKind.Client,
        GameDir = Directory.CreateDirectory(Path.Combine(_root, "game-" + name)).FullName,
        DataDir = Directory.CreateDirectory(Path.Combine(_root, "data-" + name)).FullName,
    });

    /// <summary>Серверный профиль со своей папкой внутри …\ServerProfiles — такую программа заводит сама.</summary>
    private ProfileViewModel OwnServer(string name) => Vm(new GameProfile
    {
        Id = "pa-" + Guid.NewGuid().ToString("N"),
        Name = name,
        Kind = ProfileKind.Server,
        DataDir = Directory.CreateDirectory(Path.Combine(_root, "VintagestoryData", ServerProfileLayout.ContainerName, name)).FullName,
    });

    /// <summary>Дождаться ответа в потоке окна (его продолжения идут через ту же очередь).</summary>
    private static T Done<T>(Task<T> task)
    {
        Sta.Wait(task);
        return task.Result;
    }

    private static void Windows(params (string Method, Func<object?[], object?> Answer)[] answers) =>
        AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of(answers);

    // ---- новый серверный профиль

    [Fact]
    public void NewServer_Cancelled_AddsNothing() => Sta.Run(() =>
    {
        Windows((nameof(IAppWindows.NewProfile), _ => null));
        var actions = new ProfileActions([], () => null);

        Assert.Null(actions.NewServer());
    });

    // сервер на другом компьютере: папок нет, код подключения — зашифрованным
    [Fact]
    public void NewServer_Remote_KeepsOnlyTheEncryptedCode() => Sta.Run(() =>
    {
        var code = new ConnectionCode("203.0.113.7", 42421, "ключ", new string('a', 64)); // отпечаток — 64 знака, как SHA-256 в hex
        Windows((nameof(IAppWindows.NewProfile), _ => new NewProfileChoice("Дальний", Path.Combine(_root, "unused"), ExistingProfileMode.Remote, code, null)));
        var actions = new ProfileActions([], () => null);

        var profile = actions.NewServer();

        Assert.NotNull(profile);
        Assert.Equal("Дальний", profile.Name);
        Assert.Equal(ProfileKind.Server, profile.Kind);
        Assert.Null(profile.DataDir);
        Assert.Null(profile.GameDir);
        Assert.NotEqual("ключ", profile.RemoteCode);
        Assert.Equal(code, RemoteSecret.Unprotect(profile.RemoteCode));
        Assert.False(Directory.Exists(Path.Combine(_root, "unused")));
    });

    // папка данных сервера уже есть — берётся как есть; папка игры — как у активного профиля
    [Fact]
    public void NewServer_UseInPlace_TakesTheFolder_AndTheGameOfTheActiveProfile() => Sta.Run(() =>
    {
        var existing = Directory.CreateDirectory(Path.Combine(_root, "old-server")).FullName;
        var active = ClientIn("Игра");
        Windows((nameof(IAppWindows.NewProfile), _ => new NewProfileChoice("Старый", Path.Combine(_root, "unused"), ExistingProfileMode.UseInPlace, null, existing)));
        var actions = new ProfileActions([active], () => active);

        var profile = actions.NewServer();

        Assert.Equal(existing, profile?.DataDir);
        Assert.Equal(active.GameDir, profile?.GameDir);
    });

    // своя новая папка данных создаётся сразу: профиль с несуществующей папкой выглядел бы сломанным
    [Fact]
    public void NewServer_New_CreatesItsDataFolder() => Sta.Run(() =>
    {
        var dataDir = Path.Combine(_root, "VintagestoryData", ServerProfileLayout.ContainerName, "Новый");
        Windows((nameof(IAppWindows.NewProfile), args =>
        {
            Assert.Equal(ProfileKind.Server, args[0]);
            Assert.Equal(Loc.T("profile.newServer", 2), args[1]); // номер — следующий после уже существующих профилей
            return new NewProfileChoice("Новый", dataDir, ExistingProfileMode.None, null, null);
        }));
        var actions = new ProfileActions([ClientIn("Игра")], () => null);

        var profile = actions.NewServer();

        Assert.Equal(dataDir, profile?.DataDir);
        Assert.True(Directory.Exists(dataDir));
    });

    // ---- новый клиентский профиль

    [Fact]
    public void NewClient_IsBasedOnTheActiveClient() => Sta.Run(() =>
    {
        var first = ClientIn("Первый");
        var active = ClientIn("Активный");
        GameProfile? source = null;
        var made = new GameProfile { Name = "Копия", Kind = ProfileKind.Client };
        Windows((nameof(IAppWindows.ClientProfile), args =>
        {
            source = (GameProfile)args[0]!;
            Assert.False((bool)args[1]!); // новый профиль, не клон
            Assert.Equal(Loc.T("profile.newClient", 3), args[2]);
            return made;
        }));
        var actions = new ProfileActions([first, active], () => active);

        Assert.Same(made, actions.NewClient());
        Assert.Same(active.Model, source);
    });

    // активный — сервер: основа — первый клиентский
    [Fact]
    public void NewClient_ActiveIsServer_IsBasedOnTheFirstClient() => Sta.Run(() =>
    {
        var server = OwnServer("Сервер");
        var client = ClientIn("Игра");
        GameProfile? source = null;
        Windows((nameof(IAppWindows.ClientProfile), args =>
        {
            source = (GameProfile)args[0]!;
            return null; // отменили
        }));
        var actions = new ProfileActions([server, client], () => server);

        Assert.Null(actions.NewClient());
        Assert.Same(client.Model, source);
    });

    // ---- клон

    // удалённый сервер клонируется на своём компьютере: ни окна, ни профиля
    [Fact]
    public void Clone_RemoteOrNothing_DoesNothing() => Sta.Run(() =>
    {
        Windows(); // любое окно — ошибка теста
        var actions = new ProfileActions([], () => null);
        var remote = Vm(new GameProfile { Id = "pa-remote", Name = "Дальний", Kind = ProfileKind.Server, RemoteCode = "не-код" });

        Assert.Null(Done(actions.CloneAsync(remote)));
        Assert.Null(Done(actions.CloneAsync(null)));
    });

    [Fact]
    public void Clone_Server_AsksTheCloneWindow_ServerNotRunning() => Sta.Run(() =>
    {
        var server = OwnServer("Сервер");
        var clone = new GameProfile { Name = "Сервер 2", Kind = ProfileKind.Server };
        Windows((nameof(IAppWindows.CloneServerProfile), args =>
        {
            Assert.Same(server.Model, args[0]);
            Assert.False((bool)args[1]!); // агента нет — сервер не работает
            return clone;
        }));
        var actions = new ProfileActions([server], () => server);

        Assert.Same(clone, Done(actions.CloneAsync(server)));
    });

    // ---- удаление

    // своя папка данных: вопрос «удалить и папку?» с ответом по умолчанию «Нет»; «Отмена» — профиль остаётся
    [Fact]
    public void Remove_OwnFolder_Cancel_KeepsTheProfileAndTheFolder() => Sta.Run(() =>
    {
        var server = OwnServer("Сервер");
        using var script = new ScriptedDialogs(Answer.Cancel);
        var actions = new ProfileActions([server, ClientIn("Игра")], () => null);

        Assert.False(Done(actions.PrepareRemoveAsync(server)));

        Assert.Single(script.Asked);
        Assert.True(Directory.Exists(server.Model.DataDir));
    });

    [Fact]
    public void Remove_OwnFolder_Yes_TrashesTheFolder() => Sta.Run(() =>
    {
        var server = OwnServer("Сервер");
        File.WriteAllText(Path.Combine(server.Model.DataDir!, "serverconfig.json"), "{}");
        using var script = new ScriptedDialogs(Answer.Yes);
        var actions = new ProfileActions([server, ClientIn("Игра")], () => null);

        Assert.True(Done(actions.PrepareRemoveAsync(server)));

        Assert.False(Directory.Exists(server.Model.DataDir));
    });

    [Fact]
    public void Remove_OwnFolder_No_RemovesOnlyTheProfile() => Sta.Run(() =>
    {
        var server = OwnServer("Сервер");
        using var script = new ScriptedDialogs(Answer.No);
        var actions = new ProfileActions([server, ClientIn("Игра")], () => null);

        Assert.True(Done(actions.PrepareRemoveAsync(server)));

        Assert.True(Directory.Exists(server.Model.DataDir));
    });

    // папкой пользуется другой профиль — удалить её не предлагаем, только подтвердить удаление профиля
    [Fact]
    public void Remove_FolderUsedByAnotherProfile_OnlyConfirms_AndKeepsTheFolder() => Sta.Run(() =>
    {
        var server = OwnServer("Сервер");
        var user = Vm(new GameProfile { Id = "pa-user", Name = "Тот же мир", Kind = ProfileKind.Server, DataDir = server.Model.DataDir });
        using var script = new ScriptedDialogs(Answer.No);
        var actions = new ProfileActions([server, user], () => null);

        Assert.False(Done(actions.PrepareRemoveAsync(server)));

        Assert.Contains("Тот же мир", Assert.Single(script.Asked));
        Assert.True(Directory.Exists(server.Model.DataDir));
    });

    // чужую папку (не в …\ClientProfiles) программа не трогает никогда: только «удалить профиль?»
    [Fact]
    public void Remove_ForeignFolder_Confirmed_KeepsTheFolder() => Sta.Run(() =>
    {
        var client = ClientIn("Игра");
        using var script = new ScriptedDialogs(Answer.Yes);
        var actions = new ProfileActions([client, ClientIn("Другая")], () => client);

        Assert.True(Done(actions.PrepareRemoveAsync(client)));

        Assert.Equal(Loc.T("settings.removeAsk", client.Name, client.Model.DataDir), Assert.Single(script.Asked));
        Assert.True(Directory.Exists(client.Model.DataDir));
    });
}

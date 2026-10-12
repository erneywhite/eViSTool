using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;
using Newtonsoft.Json.Linq;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Окно «Найти сервер»: то, что в окне на WPF делали код окна и привязки, — вкладки и сортировка в обе стороны, пауза
/// в поиске, кнопки строк («Играть», «В избранное», «Изменить», «Добавить сервер») и закрытие окна после запуска игры.
/// Сети нет: общий список заполняется в тесте строками, а не с мастер-сервера. Избранное — настоящий clientsettings.json
/// во временной папке. Окна пароля и избранного подменены; игра не запускается ни в одном тесте.
/// </summary>
public sealed class FindServerViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-fsvm-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly IAppWindows? _oldWindows = AppWindows.IsSet ? AppWindows.Current : null;

    public FindServerViewModelTests()
    {
        _data = Directory.CreateDirectory(Path.Combine(_root, "VintagestoryData")).FullName;
        Directory.CreateDirectory(Path.Combine(_root, "game"));
    }

    public void Dispose()
    {
        AppWindows.Current = _oldWindows!; // null — не было: следующий тест поставит своё
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string SettingsFile => Path.Combine(_data, "clientsettings.json");

    /// <summary>Избранное игры — строками «имя,адрес,пароль», как его хранит сама игра.</summary>
    private void WriteFavorites(params string[] lines) =>
        File.WriteAllText(SettingsFile, new JObject { ["stringListSettings"] = new JObject { ["multiplayerservers"] = new JArray(lines) } }.ToString());

    private string[] ReadFavorites() =>
        [.. ((JArray)JObject.Parse(File.ReadAllText(SettingsFile))["stringListSettings"]!["multiplayerservers"]!).Select(t => t.Value<string>()!)];

    /// <summary>
    /// Модель на клиентском профиле во временной папке (игры там нет — версия игры неизвестна, фильтр «моя версия» не
    /// действует). main — только для «Играть»: без него модель до запуска игры дойти не может.
    /// </summary>
    private FindServerViewModel Model(MainViewModel? main = null)
    {
        var client = new GameProfile
        {
            Id = "fsvm-client",
            Name = "Игра",
            Kind = ProfileKind.Client,
            GameDir = Path.Combine(_root, "game"),
            DataDir = _data,
        };
        return new FindServerViewModel(main!, new ProfileViewModel(client, _ => { }));
    }

    /// <summary>
    /// Главная модель без конструктора: активного профиля нет, поэтому «сыграть» доходит до неё и ничего не запускает
    /// (LaunchAsync выходит на первой строке) — ни настроек, ни игры, ни таймеров.
    /// </summary>
    private static MainViewModel IdleMain() => (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));

    /// <summary>Сервер общего списка — строкой окна, как после загрузки (в порядке «по игрокам»).</summary>
    private static ServerRowViewModel Public(FindServerViewModel vm, string name, string address, int players = 5, bool password = false,
        string description = "") =>
        new(vm, new PublicServer(name, address, "surviveandbuild", [], players, 32, "1.21.5", password, false, description), null);

    /// <summary>Подменить окна программы на время теста.</summary>
    private static Fake Windows(Func<string, string?>? password = null, Func<PlayTarget?, FavoriteServerChoice?>? edit = null)
    {
        var fake = Fake.Create(password, edit);
        AppWindows.Current = (IAppWindows)fake;
        return fake;
    }

    // ---- вкладки и сортировка: переключатели привязаны в обе стороны

    [Fact]
    public void IsAllTab_IsTheOtherSideOfFavoritesTab_BothWays() => Sta.Run(() =>
    {
        var vm = Model();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(vm.IsAllTab);
        Assert.False(vm.IsFavoritesTab);

        vm.IsAllTab = false; // нажали «Избранное»: переключатель «Все серверы» выключился
        Assert.True(vm.IsFavoritesTab);
        Assert.Contains(nameof(FindServerViewModel.IsAllTab), changed);

        vm.IsAllTab = true; // нажали «Все серверы»
        Assert.False(vm.IsFavoritesTab);

        vm.IsFavoritesTab = true;
        Assert.False(vm.IsAllTab);
    });

    [Fact]
    public void ByPlayers_IsTheOtherSideOfByName_AndSortsTheList() => Sta.Run(() =>
    {
        var vm = Model();
        // как после загрузки: по игрокам, больше — выше
        vm.All.Add(Public(vm, "Zeta", "203.0.113.1", players: 50));
        vm.All.Add(Public(vm, "alpha", "203.0.113.2", players: 30));
        vm.All.Add(Public(vm, "Beta", "203.0.113.3", players: 10));
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(vm.ByPlayers);
        Assert.Equal(new[] { "Zeta", "alpha", "Beta" }, vm.View.Items.Select(r => r.Name));

        vm.ByName = true; // нажали «По имени»
        Assert.False(vm.ByPlayers);
        Assert.Contains(nameof(FindServerViewModel.ByPlayers), changed);
        Assert.Equal(new[] { "alpha", "Beta", "Zeta" }, vm.View.Items.Select(r => r.Name));

        changed.Clear();
        vm.ByPlayers = true; // нажали «По игрокам»: порядок источника
        Assert.False(vm.ByName);
        Assert.Contains(nameof(FindServerViewModel.ByPlayers), changed);
        Assert.Equal(new[] { "Zeta", "alpha", "Beta" }, vm.View.Items.Select(r => r.Name));

        vm.ByPlayers = false;
        Assert.True(vm.ByName);
    });

    // ---- поиск: фильтр — после паузы в печати

    [Fact]
    public void SearchInput_ReachesTheFilter_OnlyAfterThePause_AndOnlyTheLastText() => Sta.Run(() =>
    {
        var vm = Model();
        vm.All.Add(Public(vm, "Лесная деревня", "203.0.113.1"));
        vm.All.Add(Public(vm, "Пустыня", "203.0.113.2", description: "Песок и лес у оазиса"));
        vm.All.Add(Public(vm, "Горы", "203.0.113.3"));
        var searches = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FindServerViewModel.Search)) searches.Add(vm.Search); };

        var clock = Stopwatch.StartNew();
        vm.SearchInput = "л";
        vm.SearchInput = "ле";
        vm.SearchInput = "лес";

        // сразу — ничего: список ещё полный
        Assert.Equal("", vm.Search);
        Assert.Equal(3, vm.View.Count);

        Sta.WaitFor(() => vm.Search == "лес");
        Assert.True(clock.Elapsed >= FindServerViewModel.SearchDelay - TimeSpan.FromMilliseconds(20), $"фильтр раньше паузы: {clock.Elapsed}");
        Assert.Equal(new[] { "лес" }, searches); // промежуточные «л» и «ле» до фильтра не дошли
        // ищет и в названии, и в описании
        Assert.Equal(new[] { "Лесная деревня", "Пустыня" }, vm.View.Items.Select(r => r.Name));
        Assert.Equal(Loc.T("browse.shown", 2, 3), vm.CountText);

        vm.SearchInput = "";
        Sta.WaitFor(() => vm.Search == "");
        Assert.Equal(3, vm.View.Count);
    });

    // ---- «Играть»

    [Fact]
    public void Play_ServerWithPassword_Cancelled_KeepsTheWindowOpen() => Sta.Run(() =>
    {
        var fake = Windows(password: _ => null);
        var vm = Model(); // без главной модели: до запуска дойти не должно
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        Sta.Wait(vm.PlayCommand.ExecuteAsync(Public(vm, "Закрытый", "203.0.113.9", password: true)));

        Assert.Equal(new[] { "Закрытый" }, fake.PasswordAsked);
        Assert.Equal(0, closed);
    });

    [Fact]
    public void Play_ServerWithPassword_Entered_ClosesTheWindow() => Sta.Run(() =>
    {
        var fake = Windows(password: _ => "секрет");
        var vm = Model(IdleMain());
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        Sta.Wait(vm.PlayCommand.ExecuteAsync(Public(vm, "Закрытый", "203.0.113.9", password: true)));

        Assert.Equal(new[] { "Закрытый" }, fake.PasswordAsked);
        Assert.Equal(1, closed);
    });

    [Fact]
    public void Play_ServerWithoutPassword_DoesNotAsk_AndClosesTheWindow() => Sta.Run(() =>
    {
        var fake = Windows(); // спросить пароль — ошибка теста
        var vm = Model(IdleMain());
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        Sta.Wait(vm.PlayCommand.ExecuteAsync(Public(vm, "Открытый", "203.0.113.10")));

        Assert.Empty(fake.PasswordAsked);
        Assert.Equal(1, closed);
    });

    [Fact]
    public void Play_Favorite_UsesItsSavedPassword_AndClosesTheWindow() => Sta.Run(() =>
    {
        WriteFavorites("Друзья,play.example.org:42420,секрет");
        var fake = Windows(); // пароль у избранного уже есть — не спрашивать
        var vm = Model(IdleMain());
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;
        var row = Assert.Single(vm.Favorites);
        Assert.True(row.HasPassword);

        Sta.Wait(vm.PlayCommand.ExecuteAsync(row));

        Assert.Empty(fake.PasswordAsked);
        Assert.Equal(1, closed);
    });

    [Fact]
    public void Play_WithoutRow_DoesNothing() => Sta.Run(() =>
    {
        Windows();
        var vm = Model();
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        Sta.Wait(vm.PlayCommand.ExecuteAsync(null));

        Assert.Equal(0, closed);
    });

    // ---- «В избранное» в общем списке

    [Fact]
    public void AddToFavorites_ServerWithPassword_SavesItWithThePassword() => Sta.Run(() =>
    {
        WriteFavorites("Дом,192.168.1.20,");
        var fake = Windows(password: _ => "секрет");
        var vm = Model();
        var row = Public(vm, "Закрытый", "203.0.113.9", password: true);
        vm.All.Add(row);

        vm.AddToFavoritesCommand.Execute(row);

        Assert.Equal(new[] { "Закрытый" }, fake.PasswordAsked);
        Assert.Equal(new[] { "Дом,192.168.1.20,", "Закрытый,203.0.113.9,секрет" }, ReadFavorites());
        Assert.True(row.InFavorites);
        Assert.Equal(new[] { "Дом", "Закрытый" }, vm.Favorites.Select(r => r.Name));
        Assert.Equal(Loc.T("browse.tabFavorites", 2), vm.FavoritesTabTitle);
        Assert.Equal(Loc.T("browse.added", "Закрытый"), vm.StatusText);
        Assert.Equal("", vm.ErrorText);
    });

    [Fact]
    public void AddToFavorites_PasswordCancelled_SavesNothing() => Sta.Run(() =>
    {
        WriteFavorites("Дом,192.168.1.20,");
        var fake = Windows(password: _ => null);
        var vm = Model();
        var row = Public(vm, "Закрытый", "203.0.113.9", password: true);

        vm.AddToFavoritesCommand.Execute(row);

        Assert.Single(fake.PasswordAsked);
        Assert.Equal(new[] { "Дом,192.168.1.20," }, ReadFavorites());
        Assert.False(row.InFavorites);
        Assert.Single(vm.Favorites);
        Assert.Equal("", vm.StatusText);
    });

    [Fact]
    public void AddToFavorites_ServerWithoutPassword_DoesNotAsk() => Sta.Run(() =>
    {
        WriteFavorites();
        var fake = Windows();
        var vm = Model();
        var row = Public(vm, "Открытый", "203.0.113.10");

        vm.AddToFavoritesCommand.Execute(row);

        Assert.Empty(fake.PasswordAsked);
        Assert.Equal(new[] { "Открытый,203.0.113.10," }, ReadFavorites());
        Assert.True(row.InFavorites);
    });

    [Fact]
    public void AddToFavorites_GameSettingsMissing_ShowsTheError() => Sta.Run(() =>
    {
        Windows(); // clientsettings.json нет: игру ни разу не запускали
        var vm = Model();
        var row = Public(vm, "Открытый", "203.0.113.10");

        vm.AddToFavoritesCommand.Execute(row);

        Assert.Equal(Loc.T("browse.noSettings"), vm.ErrorText);
        Assert.False(row.InFavorites);
        Assert.False(File.Exists(SettingsFile));
    });

    // ---- «Добавить сервер» и «Изменить» на вкладке избранного

    [Fact]
    public void AddManual_SavesTheNewServer_AndMarksItInTheList() => Sta.Run(() =>
    {
        WriteFavorites("Дом,192.168.1.20,");
        var fake = Windows(edit: _ => new FavoriteServerChoice("Новый", "203.0.113.5:42420", "пароль"));
        var vm = Model();
        var listed = Public(vm, "Новый в списке", "203.0.113.5:42420");
        vm.All.Add(listed);

        vm.AddManualCommand.Execute(null);

        Assert.Null(Assert.Single(fake.Edited)); // окно открыли пустым: новая запись
        Assert.Equal(new[] { "Дом,192.168.1.20,", "Новый,203.0.113.5:42420,пароль" }, ReadFavorites());
        Assert.Equal(new[] { "Дом", "Новый" }, vm.Favorites.Select(r => r.Name));
        Assert.True(listed.InFavorites); // тот же адрес в общем списке — «В избранном»
        Assert.Equal(Loc.T("browse.added", "Новый"), vm.StatusText);
    });

    [Fact]
    public void AddManual_Cancelled_ChangesNothing() => Sta.Run(() =>
    {
        WriteFavorites("Дом,192.168.1.20,");
        var fake = Windows(edit: _ => null);
        var vm = Model();

        vm.AddManualCommand.Execute(null);

        Assert.Single(fake.Edited);
        Assert.Equal(new[] { "Дом,192.168.1.20," }, ReadFavorites());
        Assert.Single(vm.Favorites);
        Assert.Equal("", vm.StatusText);
        Assert.Equal("", vm.ErrorText);
    });

    [Fact]
    public void AddManual_GameSettingsMissing_ShowsTheError() => Sta.Run(() =>
    {
        Windows(edit: _ => new FavoriteServerChoice("Новый", "203.0.113.5", null));
        var vm = Model();

        vm.AddManualCommand.Execute(null);

        Assert.Equal(Loc.T("browse.noSettings"), vm.ErrorText);
        Assert.Empty(vm.Favorites);
    });

    [Fact]
    public void EditFavorite_RewritesTheEntryInPlace() => Sta.Run(() =>
    {
        WriteFavorites("Дом,192.168.1.20,", "Друзья,play.example.org,старый");
        var fake = Windows(edit: _ => new FavoriteServerChoice("Дача", "192.168.1.21", null));
        var vm = Model();
        var old = Public(vm, "Дом в списке", "192.168.1.20");
        var moved = Public(vm, "Дача в списке", "192.168.1.21");
        vm.All.Add(old);
        vm.All.Add(moved);
        old.InFavorites = true;

        vm.EditFavoriteCommand.Execute(vm.Favorites[0]);

        // окно получило прежнюю запись, а в файле она поменялась на своём месте
        Assert.Equal("192.168.1.20", Assert.Single(fake.Edited)!.Address);
        Assert.Equal(new[] { "Дача,192.168.1.21,", "Друзья,play.example.org,старый" }, ReadFavorites());
        Assert.Equal(new[] { "Дача", "Друзья" }, vm.Favorites.Select(r => r.Name));
        // отметки «В избранном» в общем списке — по новому адресу
        Assert.False(old.InFavorites);
        Assert.True(moved.InFavorites);
    });

    [Fact]
    public void EditFavorite_RowOfThePublicList_OrNoRow_OpensNothing() => Sta.Run(() =>
    {
        WriteFavorites("Дом,192.168.1.20,");
        var fake = Windows();
        var vm = Model();

        vm.EditFavoriteCommand.Execute(Public(vm, "Не из избранного", "203.0.113.1"));
        vm.EditFavoriteCommand.Execute(null);

        Assert.Empty(fake.Edited);
        Assert.Equal(new[] { "Дом,192.168.1.20," }, ReadFavorites());
    });

    [Fact]
    public void AddToFavorites_WithoutRow_DoesNothing() => Sta.Run(() =>
    {
        WriteFavorites();
        var fake = Windows();
        var vm = Model();

        vm.AddToFavoritesCommand.Execute(null);

        Assert.Empty(fake.PasswordAsked);
        Assert.Empty(ReadFavorites());
    });

    /// <summary>
    /// Окна программы на время теста: пароль сервера и запись избранного отвечают заданным, что спросили — запоминается;
    /// любое другое окно роняет тест. Открытый и вложенный: DispatchProxy строит наследника в своей сборке.
    /// </summary>
    public class Fake : DispatchProxy
    {
        private Func<string, string?> _password = server => throw new InvalidOperationException("Пароль в этом тесте не ожидался: " + server);
        private Func<PlayTarget?, FavoriteServerChoice?> _edit = _ => throw new InvalidOperationException("Окно избранного в этом тесте не ожидалось");

        public List<string> PasswordAsked { get; } = [];
        public List<PlayTarget?> Edited { get; } = [];

        public static Fake Create(Func<string, string?>? password, Func<PlayTarget?, FavoriteServerChoice?>? edit)
        {
            var fake = (Fake)(object)Create<IAppWindows, Fake>();
            if (password is not null) fake._password = password;
            if (edit is not null) fake._edit = edit;
            return fake;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IAppWindows.AskServerPassword):
                    var server = (string)args![0]!;
                    PasswordAsked.Add(server);
                    return _password(server);
                case nameof(IAppWindows.EditFavoriteServer):
                    var existing = (PlayTarget?)args![0];
                    Edited.Add(existing);
                    return _edit(existing);
                default:
                    throw new InvalidOperationException($"Неожиданный вызов IAppWindows.{targetMethod?.Name}");
            }
        }
    }
}

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Desktop.Controls;
using eViSTool.Desktop.Platform;
using eViSTool.Desktop.Views;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Вкладка «Игроки» раздела «Сервер» на Avalonia: настоящая модель читает файлы серверной папки (сервер остановлен —
/// правка идёт в файлы), окно в тесте с лесной темой. Проверяем то, что компилятор разметки не видит: что кнопки строк
/// доходят до команд модели через предка, что выбор роли и переключатели пишут в файлы и что загрузка списка сама
/// ничего не меняет.
/// </summary>
public sealed class ServerPlayersTabTests
{
    private sealed class Rig : IDisposable
    {
        private static string Stamp(TimeSpan fromNow) => DateTime.UtcNow.Add(fromNow).ToString("o");

        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "evistool-tests", "players-" + Guid.NewGuid().ToString("N"));
        public ServerPlayersViewModel Model { get; }
        public ServerPlayersTab Tab { get; }
        public Window Window { get; }

        public Rig()
        {
            if (!Ui.IsSet) Ui.Platform = new AvaloniaUiPlatform();
            Directory.CreateDirectory(Path.Combine(Dir, "Playerdata"));
            File.WriteAllText(Path.Combine(Dir, "serverconfig.json"), """
                { "WhitelistMode": 2, "Roles": [
                    { "Code": "suplayer", "Name": "Survival Player" }, { "Code": "crplayer", "Name": "Creative Player" }, { "Code": "admin", "Name": "Admin" } ] }
                """);
            // по последнему заходу: Erney, Лев, Marta — у Marta действующий бан, в белом списке — Erney, Marta и тот, кто ни разу не заходил
            File.WriteAllText(Path.Combine(Dir, "Playerdata", "playerdata.json"), $$"""
                [ { "PlayerUID": "u1", "LastKnownPlayername": "Erney", "RoleCode": "admin", "LastJoinDate": "{{Stamp(TimeSpan.FromHours(-3))}}" },
                  { "PlayerUID": "u2", "LastKnownPlayername": "Лев", "RoleCode": "suplayer", "LastJoinDate": "{{Stamp(TimeSpan.FromDays(-1))}}" },
                  { "PlayerUID": "u3", "LastKnownPlayername": "Marta", "RoleCode": "crplayer", "LastJoinDate": "{{Stamp(TimeSpan.FromDays(-5))}}" } ]
                """);
            File.WriteAllText(Path.Combine(Dir, "Playerdata", "playerswhitelisted.json"), $$"""
                [ { "PlayerUID": "u1", "PlayerName": "Erney", "UntilDate": "{{Stamp(TimeSpan.FromDays(365 * 50))}}" },
                  { "PlayerUID": "u3", "PlayerName": "Marta", "UntilDate": "{{Stamp(TimeSpan.FromDays(365 * 50))}}" },
                  { "PlayerUID": "u9", "PlayerName": "NeverJoined", "UntilDate": "{{Stamp(TimeSpan.FromDays(365 * 50))}}" } ]
                """);
            File.WriteAllText(Path.Combine(Dir, "Playerdata", "playersbanned.json"), $$"""
                [ { "PlayerUID": "u3", "PlayerName": "Marta", "UntilDate": "{{Stamp(TimeSpan.FromDays(2))}}", "Reason": "griefing", "IssuedByPlayerName": "Console" } ]
                """);

            Model = new ServerViewModel(null!).Players;
            Model.OnProfileSwitched(new GameProfile { Kind = ProfileKind.Server, DataDir = Dir });
            Tab = new ServerPlayersTab { DataContext = Model };
            Window = new Window { Width = 1100, Height = 1000, Content = Tab };
            // тема окна: шаблоны и ключи, на которые ссылается разметка вкладки (в тестовом приложении только Fluent)
            var host = new Uri("avares://eViSTool.Desktop.Tests");
            Window.Resources.MergedDictionaries.Add(new ResourceInclude(host) { Source = new Uri("avares://eViSTool/Themes/ForestResources.axaml") });
            Window.Resources.MergedDictionaries.Add(new ResourceInclude(host) { Source = new Uri("avares://eViSTool/Themes/ForestControls.axaml") });
            Window.Styles.Add(new StyleInclude(host) { Source = new Uri("avares://eViSTool/Themes/Forest.axaml") });
            Window.Show();
            Model.SetActive(true);
        }

        public async Task LoadedAsync()
        {
            await Wait.Until(() => Model.Loaded);
            await Wait.Idle(100);
        }

        public string Read(params string[] path) => File.ReadAllText(Path.Combine([Dir, .. path]));

        /// <summary>Все четыре файла сервера одним текстом: не изменился ли хоть один.</summary>
        public string Files() => string.Concat(new[] { "serverconfig.json", "Playerdata/playerdata.json", "Playerdata/playerswhitelisted.json", "Playerdata/playersbanned.json" }
            .Select(f => Read(f.Split('/'))));

        public IEnumerable<T> All<T>() where T : Visual => Tab.GetVisualDescendants().OfType<T>();

        /// <summary>
        /// Кнопка-ссылка в списках с этим текстом, которую сейчас видно (в строке рядом лежат «Забанить…» и «Разбанить» — видна одна).
        /// Кнопки карточки «Доступ» с тем же текстом («Забанить…») имеют AutomationId — их здесь нет.
        /// </summary>
        public IEnumerable<Button> Buttons(string key) => All<Button>().Where(b => b.IsEffectivelyVisible && b.Content as string == Loc.T(key)
                                                                                   && string.IsNullOrEmpty(AutomationProperties.GetAutomationId(b)));

        public void Dispose()
        {
            Window.Close();
            try { Directory.Delete(Dir, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    [AvaloniaFact]
    public async Task Shows_players_roles_and_both_lists()
    {
        using var rig = new Rig();
        var before = rig.Files();
        await rig.LoadedAsync();

        // по последнему заходу; в каждой строке выбрана роль игрока
        var combos = rig.All<ComboBox>().ToList();
        Assert.Equal(["admin", "suplayer", "crplayer"], combos.Select(c => ((ServerRole)c.SelectedItem!).Code));
        Assert.Equal("Admin", ((ServerRole)combos[0].SelectedItem!).Name);

        // переключатель «только по белому списку» взят из serverconfig.json
        var mode = rig.All<CheckBox>().Single(c => AutomationProperties.GetAutomationId(c) == "PlayersWhitelistMode");
        Assert.True(mode.IsChecked);
        Assert.True(mode.IsEnabled);

        // нижние списки: в белом — трое (в том числе тот, кто не заходил), в банах — один
        Assert.Equal(3, rig.Model.Whitelist.Count);
        Assert.Single(rig.Model.Bans);
        Assert.Equal(2, rig.Buttons("players.ban").Count()); // «Забанить…» — у Erney и Льва, у забаненной Marta вместо неё «Разбанить»
        Assert.Equal(2, rig.Buttons("players.unban").Count()); // в строке Marta и в списке банов

        // сам по себе показ ничего не пишет: Avalonia не сбрасывает выбранную роль, пока строка собирается
        Assert.Empty(rig.Model.ErrorText);
        Assert.Equal(before, rig.Files());
    }

    [AvaloniaFact]
    public async Task Role_and_whitelist_switches_write_files()
    {
        using var rig = new Rig();
        await rig.LoadedAsync();

        var lev = rig.All<ComboBox>().Skip(1).First();
        lev.SelectedItem = lev.ItemsSource!.Cast<ServerRole>().First(r => r.Code == "crplayer");
        await Wait.Until(() => rig.Model.Players.Count == 3 && rig.Model.Players[1].Player.Role == "crplayer"); // список перечитан из файла
        await rig.LoadedAsync();

        // переключатель в строке Erney (первая строка, в белом списке) — убрать из белого списка
        var row = rig.All<CheckBox>().First(c => AutomationProperties.GetAutomationId(c) != "PlayersWhitelistMode");
        Assert.True(row.IsChecked);
        row.IsChecked = false;
        await Wait.Until(() => !rig.Read("Playerdata", "playerswhitelisted.json").Contains("\"u1\""));
        await rig.LoadedAsync();
        Assert.Equal(2, rig.Model.Whitelist.Count);

        // общий режим — в serverconfig.json
        rig.All<CheckBox>().Single(c => AutomationProperties.GetAutomationId(c) == "PlayersWhitelistMode").IsChecked = false;
        await Wait.Until(() => rig.Read("serverconfig.json").Contains("\"WhitelistMode\": 1"));
    }

    [AvaloniaFact]
    public async Task Unban_and_remove_buttons_reach_the_commands_through_the_rows()
    {
        using var rig = new Rig();
        await rig.LoadedAsync();

        // «Разбанить» в строке игрока
        Click(rig.Window, rig.Buttons("players.unban").First());
        await Wait.Until(() => rig.Model.Bans.Count == 0 && rig.Model.Players[2].Player.Ban is null);
        await rig.LoadedAsync();
        Assert.Empty(rig.Buttons("players.unban"));
        Assert.Equal(3, rig.Buttons("players.ban").Count()); // теперь «Забанить…» у всех троих

        // «Убрать» в белом списке — у того, кто ни разу не заходил
        var remove = rig.Buttons("players.remove").ToList();
        Assert.Equal(3, remove.Count);
        Click(rig.Window, remove[^1]); // список по именам: Erney, Marta, NeverJoined
        await Wait.Until(() => rig.Model.Whitelist.Count == 2);
        Assert.DoesNotContain(rig.Model.Whitelist, w => w.Name == "NeverJoined");
    }

    [AvaloniaFact]
    public async Task Search_leaves_matching_players()
    {
        using var rig = new Rig();
        await rig.LoadedAsync();

        var search = rig.All<TextBox>().Single(t => AutomationProperties.GetAutomationId(t) == "PlayersSearch");
        search.Text = "mar";
        await Wait.Until(() => rig.All<ComboBox>().Count() == 1);
        Assert.Equal("crplayer", ((ServerRole)rig.All<ComboBox>().Single().SelectedItem!).Code);

        search.Text = "";
        await Wait.Until(() => rig.All<ComboBox>().Count() == 3);
    }

    [AvaloniaFact]
    public async Task Row_actions_follow_how_the_server_is_edited()
    {
        using var rig = new Rig();
        await rig.LoadedAsync();

        // сервер остановлен: роль, белый список и «разбанить» правят файлы, а команды (кик, бан, «класс и облик») ждут запущенный сервер
        Assert.True(rig.Model.CanEdit);
        Assert.False(rig.Model.CanCommand);
        var disabled = rig.Buttons("players.kick").Concat(rig.Buttons("players.charsel")).Concat(rig.Buttons("players.ban")).ToList();
        Assert.NotEmpty(disabled);
        Assert.All(disabled, b =>
        {
            Assert.False(b.IsEffectivelyEnabled);
            Assert.True(ToolTip.GetShowOnDisabled(b)); // подсказка объясняет, почему кнопка недоступна
        });

        // колесо над списком ролей крутит страницу: иначе случайный поворот колеса менял бы роль игрока командой серверу
        Assert.All(rig.All<ComboBox>(), c => Assert.True(WheelToParent.GetIsEnabled(c)));
    }
}

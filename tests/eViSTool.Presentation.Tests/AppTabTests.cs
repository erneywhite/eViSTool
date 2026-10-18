using eViSTool.Core.Localization;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Вкладки главного окна: порядок как в боковой панели, «Профили» отдельно от общих «Настроек», переходы «настройки
/// профиля» из других разделов ведут в «Профили». Главная модель настоящая (ей принадлежит выбранная вкладка), данные
/// она берёт из папки data рядом с тестами.
/// </summary>
public sealed class AppTabTests
{
    // порядок пунктов меню и страниц в MainWindow.axaml: окно показывает страницу по номеру вкладки
    [Fact]
    public void Tabs_GoInTheSidePanelOrder() =>
        Assert.Equal(Enumerable.Range(0, 7),
            [AppTab.Mods, AppTab.Catalog, AppTab.ModConfig, AppTab.Server, AppTab.Profiles, AppTab.Settings, AppTab.About]);

    private static void WithMain(Action<MainViewModel> test) => Sta.Run(() =>
    {
        var language = Loc.Instance.Language; // главная модель выставляет язык из настроек — другим тестам его не менять
        try
        {
            test(new MainViewModel { AutoCheckUpdates = false }); // автопроверка ходила бы в модбазу
        }
        finally { Loc.Instance.SetLanguage(language); }
    });

    [Fact]
    public void ProfileSettings_FromMyMods_OpenProfiles() => WithMain(main =>
    {
        main.SelectedTab = AppTab.Mods;

        main.Mods.OpenHostedSettingsCommand.Execute(null); // плашка «версия игры сервера не определилась»

        Assert.Equal(AppTab.Profiles, main.SelectedTab);
    });

    [Fact]
    public void ProfileSettings_FromServer_OpenProfiles() => WithMain(main =>
    {
        main.SelectedTab = AppTab.Server;

        main.Server.OpenProfilesCommand.Execute(null); // «Открыть профили» у клиента и «Подключение…» у хостера

        Assert.Equal(AppTab.Profiles, main.SelectedTab);
    });

    [Fact]
    public void PageTitle_NamesProfilesAndSettingsApart() => WithMain(main =>
    {
        main.SelectedTab = AppTab.Profiles;
        Assert.Equal(Loc.T("nav.profiles"), main.PageTitle);

        main.SelectedTab = AppTab.Settings;
        Assert.Equal(Loc.T("nav.settings"), main.PageTitle);

        main.SelectedTab = AppTab.About;
        Assert.Equal(Loc.T("nav.about"), main.PageTitle);
    });

    [Theory]
    [InlineData("profiles", AppTab.Profiles)]
    [InlineData("settings", AppTab.Settings)]
    [InlineData("about", AppTab.About)]
    public void Snapshot_ScreenOpensItsTab(string screen, int tab) => WithMain(main =>
    {
        Snapshots.Navigate(main, new SnapshotRequest(screen, "a.png", 1400, 860, TimeSpan.Zero));

        Assert.Equal(tab, main.SelectedTab);
    });
}

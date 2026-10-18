using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Settings;
using eViSTool.Desktop.Platform;
using eViSTool.Desktop.Views;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Страницы «Профили» и «Настройки» и меню главного окна с настоящей главной моделью: переключатель «Фон из игры» на
/// «Настройках» пишет в модель и в настройки, всё про профиль — только на «Профилях», порядок пунктов меню и страниц —
/// как у <see cref="AppTab"/>. Настройки программы лежат в папке data рядом с тестами: на время теста там серверный
/// профиль во временной папке, потом прежний файл возвращается. Данные пользователя тест не трогает.
/// </summary>
public sealed class ProfilesSettingsViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evistool-tests", "pages-" + Guid.NewGuid().ToString("N"));
    private readonly string _language = Loc.Instance.Language;
    private readonly List<ResourceInclude> _theme = [];
    private StyleInclude? _themeStyles;
    private string? _savedSettings;
    private Window? _window;

    public void Dispose()
    {
        _window?.Close();
        if (Application.Current is { } app)
        {
            foreach (var include in _theme) app.Resources.MergedDictionaries.Remove(include);
            if (_themeStyles is not null) app.Styles.Remove(_themeStyles);
        }
        // главная модель выставила язык из настроек и записала их — другим тестам вернуть как было
        Loc.Instance.SetLanguage(_language);
        try
        {
            if (_savedSettings is null) File.Delete(AppPaths.SettingsFile);
            else File.WriteAllText(AppPaths.SettingsFile, _savedSettings);
        }
        catch (IOException) { }
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>Лесная тема окна: страницы берут из неё переключатель, кисти и иконки, главное окно — ещё и свою шапку.</summary>
    private void AddTheme()
    {
        var app = Application.Current!;
        foreach (var name in new[] { "ForestResources", "ForestControls", "ForestWindowChrome" })
        {
            var include = new ResourceInclude(new Uri("avares://eViSTool.Desktop.Tests")) { Source = new Uri($"avares://eViSTool/Themes/{name}.axaml") };
            _theme.Add(include);
            app.Resources.MergedDictionaries.Add(include);
        }
        _themeStyles = new StyleInclude(new Uri("avares://eViSTool.Desktop.Tests")) { Source = new Uri("avares://eViSTool/Themes/Forest.axaml") };
        app.Styles.Add(_themeStyles);
    }

    /// <summary>
    /// Главная модель над своими настройками: один серверный профиль «Стенд» во временной папке, фон из игры включён,
    /// без автопроверки модов (она ходила бы в модбазу).
    /// </summary>
    private MainViewModel Main()
    {
        Assert.SkipUnless(AppPaths.IsPortable, "данные рядом с тестами недоступны для записи — настоящие данные пользователя не трогаем");
        AddTheme();
        if (!Ui.IsSet) Ui.Platform = new AvaloniaUiPlatform();
        _savedSettings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllText(AppPaths.SettingsFile) : null;

        Directory.CreateDirectory(Path.Combine(_dir, "game"));
        Directory.CreateDirectory(Path.Combine(_dir, "data"));
        var profile = new GameProfile
        {
            Id = "pages-test", Name = "Стенд", Kind = ProfileKind.Server,
            GameDir = Path.Combine(_dir, "game"), DataDir = Path.Combine(_dir, "data"),
        };
        new SettingsStore().Save(new AppSettings
        {
            Profiles = [profile], ActiveProfileId = profile.Id, Language = _language,
            AutoCheckUpdates = false, GameBackground = true,
        });
        return new MainViewModel();
    }

    private Window Show(Control content, double width = 1200, double height = 900)
    {
        _window = new Window { Width = width, Height = height, Content = content };
        _window.Show();
        Dispatcher.UIThread.RunJobs();
        return _window;
    }

    private static Control Named(Visual root, string automationId) =>
        root.GetVisualDescendants().OfType<Control>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    private static bool Has(Visual root, string automationId) =>
        root.GetVisualDescendants().OfType<Control>().Any(c => AutomationProperties.GetAutomationId(c) == automationId);

    private static bool HasText(Visual root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);

    [AvaloniaFact]
    public void Game_background_switch_on_the_settings_page_changes_the_model_and_the_settings_file()
    {
        var main = Main();
        var window = Show(new SettingsView { DataContext = main });
        var toggle = (CheckBox)Named(window, "GameBackground");
        Assert.True(main.GameBackground);
        Assert.True(toggle.IsChecked);
        Assert.True(toggle.IsEffectivelyVisible);

        Mouse.Click(window, toggle); // настоящим нажатием, как человек

        Assert.False(toggle.IsChecked);
        Assert.False(main.GameBackground);
        Assert.False(new SettingsStore().Load().GameBackground); // записано сразу, как и остальные переключатели

        main.GameBackground = true; // и обратно: модель → переключатель
        Dispatcher.UIThread.RunJobs();
        Assert.True(toggle.IsChecked);
    }

    [AvaloniaFact]
    public void Settings_page_has_only_common_blocks_and_profiles_page_has_the_profile()
    {
        var main = Main();
        var settings = Show(new SettingsView { DataContext = main });

        // блоки «Настроек»: вид, обновления, моды, оповещения, данные программы
        foreach (var key in new[] { "settings.view", "settings.updates", "settings.mods", "notify.cardTitle", "settings.appData" })
            Assert.True(HasText(settings, Loc.T(key)), key);
        Assert.True(Named(settings, "NotifyAddChannel").IsEffectivelyVisible);
        Assert.True(Named(settings, "AutoCheckUpdates").IsEffectivelyVisible);
        Assert.True(Named(settings, "AlsoWithoutAsking").IsEffectivelyVisible);
        // ничего про конкретный профиль
        Assert.DoesNotContain(settings.GetVisualDescendants().OfType<ListBox>(), l => ReferenceEquals(l.ItemsSource, main.Profiles));
        Assert.False(HasText(settings, Loc.T("settings.profileCard")));
        Assert.False(Has(settings, "CleanupDisk"));
        settings.Close();

        var profiles = Show(new ProfilesView { DataContext = main });
        var list = (ListBox)Named(profiles, "ProfileList");
        Assert.Same(main.Profiles, list.ItemsSource);
        Assert.Same(main.EditedProfile, list.SelectedItem);
        Assert.True(HasText(profiles, Loc.T("settings.profileCard")));
        Assert.Equal(main.EditedProfile!.KindText, ((TextBlock)Named(profiles, "ProfileKind")).Text);
        Assert.True(Named(profiles, "CleanupDisk").IsEffectivelyVisible);
        // а общего — нет
        Assert.False(Has(profiles, "GameBackground"));
    }

    [AvaloniaFact]
    public void Side_menu_and_pages_follow_the_tab_order_and_profiles_come_before_settings()
    {
        var main = Main();
        _window = new MainWindow { DataContext = main, Width = 1400, Height = 860 };
        _window.Show();
        Dispatcher.UIThread.RunJobs();
        var nav = _window.FindControl<ListBox>("Nav")!;
        var pages = _window.FindControl<Panel>("Pages")!;

        // страница по номеру вкладки: порядок в разметке = порядок AppTab
        Assert.Equal(7, nav.ItemCount);
        Assert.Equal(nav.ItemCount, pages.Children.Count);
        Assert.IsType<ServerView>(pages.Children[AppTab.Server]);
        Assert.IsType<ProfilesView>(pages.Children[AppTab.Profiles]);
        Assert.IsType<SettingsView>(pages.Children[AppTab.Settings]);
        Assert.IsType<AboutView>(pages.Children[AppTab.About]);

        // пункт меню «Профили» открывает свою страницу, название — в верхней полосе, без «Рабочего пространства»
        Mouse.Click(_window, Named(_window, "NavProfiles"));
        Assert.Equal(AppTab.Profiles, main.SelectedTab);
        Assert.Equal([AppTab.Profiles], pages.Children.Select((p, i) => (p, i)).Where(x => x.p.IsVisible).Select(x => x.i));
        Assert.Equal(Loc.T("nav.profiles"), ((TextBlock)Named(_window, "PageTitle")).Text);

        Mouse.Click(_window, Named(_window, "NavSettings"));
        Assert.Equal(AppTab.Settings, main.SelectedTab);
        Assert.True(pages.Children[AppTab.Settings].IsVisible);
        Assert.False(pages.Children[AppTab.Profiles].IsVisible);
        Assert.Equal(Loc.T("nav.settings"), ((TextBlock)Named(_window, "PageTitle")).Text);
    }
}

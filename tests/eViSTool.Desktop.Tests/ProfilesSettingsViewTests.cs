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
/// Главная модель сама не останавливается: её таймеры (игра, падения серверов, exe на диске, агенты, моды) создаются
/// через шпиона платформы и гасятся после теста, а тихая проверка новой версии идёт не на GitHub, а в закрытый порт.
/// </summary>
public sealed class ProfilesSettingsViewTests : IDisposable
{
    private const string ReleasesUrl = "EVISTOOL_RELEASES_URL";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evistool-tests", "pages-" + Guid.NewGuid().ToString("N"));
    private readonly string _language = Loc.Instance.Language;
    private readonly IUiPlatform? _oldPlatform = Ui.IsSet ? Ui.Platform : null;
    private readonly List<ResourceInclude> _theme = [];
    private CleanupWindowTests.TimerSpy? _timers;
    private StyleInclude? _themeStyles;
    private string? _savedSettings;
    private Window? _window;

    public void Dispose()
    {
        _window?.Close();
        // до возврата настроек: опрос, оставшийся с этого теста, мог бы записать их поверх или открыть окно в чужом тесте
        foreach (var timer in _timers?.Timers ?? []) timer.Stop();
        if (_timers is not null) Ui.Platform = _oldPlatform ?? new AvaloniaUiPlatform();
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
    /// без автопроверки модов (она ходила бы в модбазу; каталог, который тоже ходит туда, тесты не открывают).
    /// </summary>
    private MainViewModel Main()
    {
        Assert.SkipUnless(AppPaths.IsPortable, "данные рядом с тестами недоступны для записи — настоящие данные пользователя не трогаем");
        AddTheme();
        _timers = CleanupWindowTests.TimerSpy.Wrap(new AvaloniaUiPlatform());
        Ui.Platform = (IUiPlatform)(object)_timers;
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

        // адрес релизов читается ещё в конструкторе, до первого ожидания: потом переменную можно вернуть
        var releases = Environment.GetEnvironmentVariable(ReleasesUrl);
        Environment.SetEnvironmentVariable(ReleasesUrl, "http://127.0.0.1:9/releases"); // отказ — как «нет сети», тихо
        MainViewModel main;
        try { main = new MainViewModel(); }
        finally { Environment.SetEnvironmentVariable(ReleasesUrl, releases); }
        Assert.Contains(_timers.Timers, t => t.IsEnabled); // наблюдатели модели запущены, и их таймеры — у шпиона
        return main;
    }

    /// <summary>
    /// Карточка «Постройки» на «Профилях»: схема из папки WorldEdit профиля видна по имени без «.json». Других профилей
    /// нет — копировать некуда, кнопка неактивна; у профиля на этой машине есть «Открыть папку».
    /// </summary>
    [AvaloniaFact]
    public async Task Buildings_card_lists_the_profile_schematics()
    {
        var main = Main();
        var worldEdit = Directory.CreateDirectory(Path.Combine(_dir, "data", "WorldEdit")).FullName;
        File.WriteAllText(Path.Combine(worldEdit, "dom-u-reki.json"), "{}");
        await main.Schematics!.LoadAsync();
        var profiles = Show(new ProfilesView { DataContext = main });

        var card = Named(profiles, "SchematicsCard");
        Assert.True(card.IsEffectivelyVisible);
        Assert.True(HasText(card, "dom-u-reki"));
        Assert.True(HasText(card, Loc.T("schem.title")));
        var copy = card.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Loc.T("schem.copyTo")));
        Assert.False(copy.IsEffectivelyEnabled);
        Assert.Contains(card.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, Loc.T("schem.openFolder")) && b.IsEffectivelyVisible);
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
        Assert.DoesNotContain(settings.GetVisualDescendants().OfType<ListBox>(), l => ReferenceEquals(l.ItemsSource, main.SortedProfiles.Items));
        Assert.False(HasText(settings, Loc.T("settings.profileCard")));
        Assert.False(Has(settings, "CleanupDisk"));
        settings.Close();

        var profiles = Show(new ProfilesView { DataContext = main });
        var list = (ListBox)Named(profiles, "ProfileList");
        Assert.Same(main.SortedProfiles.Items, list.ItemsSource);
        // выбор порядка над списком переставляет и его
        Assert.True(Named(profiles, "ProfileSort").IsEffectivelyVisible);
        main.ProfileSortIndex = (int)eViSTool.Core.Profiles.ProfileSort.Name;
        Assert.Equal(main.Profiles.Select(p => p.Name).Order(StringComparer.CurrentCultureIgnoreCase), list.Items.Cast<ProfileViewModel>().Select(p => p.Name));
        main.ProfileSortIndex = (int)eViSTool.Core.Profiles.ProfileSort.Created;
        Assert.Same(main.EditedProfile, list.SelectedItem);
        Assert.True(HasText(profiles, Loc.T("settings.profileCard")));
        Assert.Equal(main.EditedProfile!.KindText, ((TextBlock)Named(profiles, "ProfileKind")).Text);
        Assert.True(Named(profiles, "CleanupDisk").IsEffectivelyVisible);
        // а общего — нет
        Assert.False(Has(profiles, "GameBackground"));
    }

    /// <summary>
    /// Образцы выбора значка профиля подписаны для экранного диктора словами («мох», «меч», «Без цвета»), и подписи
    /// меняются вместе с языком программы на ходу: контейнеры над постоянным списком образцов не пересоздаются.
    /// </summary>
    [AvaloniaFact]
    public void Icon_choice_names_for_the_screen_reader_follow_the_language()
    {
        var main = Main();
        Loc.Instance.SetLanguage("ru");
        var profiles = Show(new ProfilesView { DataContext = main });
        var colors = (ListBox)Named(profiles, "ProfileIconColor");
        var glyphs = (ListBox)Named(profiles, "ProfileIconGlyph");
        static string? NameOf(ListBox list, string choice) => AutomationProperties.GetName(list.ContainerFromItem(choice)!);

        Assert.Equal("мох", NameOf(colors, "moss"));
        Assert.Equal("Без цвета", NameOf(colors, ""));
        Assert.Equal("меч", NameOf(glyphs, "sword"));

        Loc.Instance.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Loc.T("icon.color.moss"), NameOf(colors, "moss"));
        Assert.Equal(Loc.T("icon.color.none"), NameOf(colors, ""));
        Assert.Equal(Loc.T("icon.glyph.sword"), NameOf(glyphs, "sword"));
        Assert.NotEqual("Без цвета", Loc.T("icon.color.none")); // и правда другой язык
    }

    /// <summary>
    /// Меню и страницы главного окна. Здесь же смена страницы (docs/design.md, раздел 3): первая страница при показе
    /// окна — сразу на месте, новая проявляется (прозрачность и сдвиг снизу) и в конце стоит на месте, прежняя прячется
    /// сразу. Отдельным тестом пока нельзя: второе показанное главное окно за один прогон тестов зависает на щелчке
    /// (зависание было и раньше, к движению оно не относится).
    /// </summary>
    [AvaloniaFact]
    public async Task Side_menu_and_pages_follow_the_tab_order_and_profiles_come_before_settings()
    {
        var main = Main();
        _window = new MainWindow { DataContext = main, Width = 1400, Height = 860 };
        _window.Show();
        Dispatcher.UIThread.RunJobs();
        var nav = _window.FindControl<ListBox>("Nav")!;
        var pages = _window.FindControl<Panel>("Pages")!;
        var first = pages.Children[main.SelectedTab];
        Assert.Equal(1, first.Opacity);
        Assert.Null(first.RenderTransform);

        // страница по номеру вкладки: порядок в разметке = порядок AppTab
        Assert.Equal(7, nav.ItemCount);
        Assert.Equal(nav.ItemCount, pages.Children.Count);
        Assert.IsType<ServerView>(pages.Children[AppTab.Server]);
        Assert.IsType<ProfilesView>(pages.Children[AppTab.Profiles]);
        Assert.IsType<SettingsView>(pages.Children[AppTab.Settings]);
        Assert.IsType<AboutView>(pages.Children[AppTab.About]);

        // пункт меню «Профили» открывает свою страницу; название — крупно на ней самой, верхняя полоса его не повторяет.
        // Проявление смотрим по пути, а не мгновенным значением: щелчок разбирает очередь окна вместе с кадрами, и
        // на большой странице оно успевает пройти целиком ещё внутри него
        var topBar = Named(_window, "TopBar");
        using (var seen = new MotionTests.RevealProbe(pages.Children[AppTab.Profiles]))
        {
            Mouse.Click(_window, Named(_window, "NavProfiles"));
            Assert.Equal(AppTab.Profiles, main.SelectedTab);
            Assert.Equal([AppTab.Profiles], pages.Children.Select((p, i) => (p, i)).Where(x => x.p.IsVisible).Select(x => x.i));
            Assert.True(HasText(pages.Children[AppTab.Profiles], Loc.T("nav.profiles")));
            Assert.False(HasText(topBar, Loc.T("nav.profiles")));
            await Wait.Frames(400);
            seen.AssertRevealed();
        }

        var settings = pages.Children[AppTab.Settings];
        using (var seen = new MotionTests.RevealProbe(settings))
        {
            Mouse.Click(_window, Named(_window, "NavSettings"));
            Assert.Equal(AppTab.Settings, main.SelectedTab);
            Assert.True(settings.IsVisible);
            Assert.False(pages.Children[AppTab.Profiles].IsVisible);
            Assert.True(HasText(settings, Loc.T("nav.settings")));
            Assert.False(HasText(topBar, Loc.T("nav.settings")));
            await Wait.Frames(400);
            seen.AssertRevealed();
        }
        Assert.Equal(1, settings.Opacity);
        Assert.Equal(0, settings.RenderTransform?.Value.M32 ?? 0);
    }

    /// <summary>
    /// Два главных окна подряд в одном процессе: первое показали и закрыли, второе работает. Раньше второе зависало на
    /// первом же разборе очереди (процесс ел ядро): радиокнопка вне дерева окна попадает в общую на весь процесс группу
    /// по GroupName, и чипы «Форма / Текст» «Настроек модов» двух окон перещёлкивали друг друга, а сверка с моделью
    /// (ModConfigView.SyncViewChips) каждый раз ставила задачу заново. Вернётся ошибка — этот тест повиснет, и прогон
    /// упадёт по --blame-hang-timeout с его именем.
    /// </summary>
    [AvaloniaFact]
    public void Two_main_windows_one_after_another_both_work()
    {
        var main = Main();
        var first = new MainWindow { DataContext = main, Width = 1400, Height = 860 };
        first.Show();
        Dispatcher.UIThread.RunJobs();
        first.Close();
        Dispatcher.UIThread.RunJobs();

        _window = new MainWindow { DataContext = main, Width = 1400, Height = 860 };
        _window.Show();
        Dispatcher.UIThread.RunJobs();
        var pages = _window.FindControl<Panel>("Pages")!;

        Mouse.Click(_window, Named(_window, "NavProfiles"));
        Assert.Equal(AppTab.Profiles, main.SelectedTab);
        Assert.True(pages.Children[AppTab.Profiles].IsVisible);

        // «Настройки модов» с теми самыми чипами — и обратно
        main.SelectedTab = AppTab.ModConfig;
        Dispatcher.UIThread.RunJobs();
        Assert.True(pages.Children[AppTab.ModConfig].IsVisible);
        Mouse.Click(_window, Named(_window, "NavMods"));
        Assert.Equal(AppTab.Mods, main.SelectedTab);
    }
}

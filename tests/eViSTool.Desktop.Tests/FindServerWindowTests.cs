using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Desktop.Dialogs;
using eViSTool.Desktop.Platform;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Окно «Найти сервер»: переключатели вкладок и сортировки привязаны к модели в обе стороны, кнопка «В избранное»
/// меняется на выключенную «В избранном», после запуска игры окно закрывается само. Сети нет: список не загружается
/// (его грузит открывающий окно метод, а здесь окно создаётся напрямую), строки кладутся в модель тестом.
/// </summary>
public sealed class FindServerWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-tests", "findserver-" + Guid.NewGuid().ToString("N"));

    public FindServerWindowTests()
    {
        // пауза поиска в модели — таймер платформы
        if (!Ui.IsSet) Ui.Platform = new AvaloniaUiPlatform();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>Модель на пустом клиентском профиле во временной папке (избранного нет, мастер-сервер не трогается).</summary>
    private FindServerViewModel Model(MainViewModel? main = null)
    {
        var client = new GameProfile
        {
            Id = "findserver-test",
            Name = "Игра",
            Kind = ProfileKind.Client,
            GameDir = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName,
            DataDir = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName,
        };
        return new FindServerViewModel(main!, new ProfileViewModel(client, _ => { }));
    }

    private static ServerRowViewModel Public(FindServerViewModel vm, string name, string address, int players) =>
        new(vm, new PublicServer(name, address, "surviveandbuild", [], players, 32, "1.21.5", false, false, ""), null);

    /// <summary>
    /// Окно с лесной темой. Ресурсы — в приложение: StaticResource в разметке окна ищет их уже при загрузке. Restore —
    /// убрать тему обратно, чтобы не менять приложение тестов для остальных проверок.
    /// </summary>
    private static (FindServerWindow Window, Action Restore) Show(FindServerViewModel model)
    {
        var app = Application.Current!;
        var baseUri = new Uri("avares://eViSTool.Desktop.Tests");
        var resources = new ResourceInclude(baseUri) { Source = new Uri("avares://eViSTool.Desktop/Themes/ForestResources.axaml") };
        var controls = new ResourceInclude(baseUri) { Source = new Uri("avares://eViSTool.Desktop/Themes/ForestControls.axaml") };
        var styles = new StyleInclude(baseUri) { Source = new Uri("avares://eViSTool.Desktop/Themes/Forest.axaml") };
        app.Resources.MergedDictionaries.Add(resources);
        app.Resources.MergedDictionaries.Add(controls);
        app.Styles.Add(styles);
        void Restore()
        {
            app.Styles.Remove(styles);
            app.Resources.MergedDictionaries.Remove(controls);
            app.Resources.MergedDictionaries.Remove(resources);
        }

        try
        {
            var window = new FindServerWindow { DataContext = model, RequestedThemeVariant = ThemeVariant.Dark };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return (window, Restore);
        }
        catch
        {
            Restore();
            throw;
        }
    }

    private static T ById<T>(Visual root, string id) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);

    /// <summary>Кнопка строки списка: в строке row, с командой command модели окна.</summary>
    private static Button RowButton(Window window, ServerRowViewModel row, System.Windows.Input.ICommand command) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.DataContext, row) && ReferenceEquals(b.Command, command));

    [AvaloniaFact]
    public void Tab_switches_follow_the_model_both_ways()
    {
        var model = Model();
        var (window, restore) = Show(model);
        try
        {
            var all = ById<RadioButton>(window, "BrowseTabAll");
            var favorites = ById<RadioButton>(window, "BrowseTabFavorites");
            var list = ById<ListBox>(window, "BrowseList");
            Assert.True(all.IsChecked);
            Assert.True(list.IsEffectivelyVisible);
            // скрытая вкладка избранного ещё не построена (шаблон прокрутки появится при показе) — кнопки не видно
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(),
                b => AutomationProperties.GetAutomationId(b) == "BrowseAddManual" && b.IsEffectivelyVisible);

            Click(window, favorites); // нажали «Избранное»
            Assert.True(model.IsFavoritesTab);
            Assert.False(all.IsChecked);
            Assert.False(list.IsEffectivelyVisible);
            Assert.True(ById<Button>(window, "BrowseAddManual").IsEffectivelyVisible);
            Assert.False(ById<TextBox>(window, "BrowseSearch").IsEffectivelyVisible); // поиск — только для общего списка

            Click(window, all); // и обратно
            Assert.False(model.IsFavoritesTab);
            Assert.False(favorites.IsChecked);
            Assert.True(list.IsEffectivelyVisible);

            model.IsFavoritesTab = true; // модель сама — переключатели за ней
            Dispatcher.UIThread.RunJobs();
            Assert.True(favorites.IsChecked);
            Assert.False(all.IsChecked);
        }
        finally
        {
            if (window.IsVisible) window.Close();
            restore();
        }
    }

    [AvaloniaFact]
    public void Sort_chips_follow_the_model_both_ways()
    {
        var model = Model();
        model.All.Add(Public(model, "Zeta", "203.0.113.1", 50));
        model.All.Add(Public(model, "alpha", "203.0.113.2", 30));
        var (window, restore) = Show(model);
        try
        {
            var chips = window.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "BrowseSort").ToList();
            var byPlayers = chips.Single(r => Equals(r.Content, Loc.T("browse.sortPlayers")));
            var byName = chips.Single(r => Equals(r.Content, Loc.T("browse.sortName")));
            Assert.True(byPlayers.IsChecked);
            Assert.False(byName.IsChecked);

            Click(window, byName);
            Assert.True(model.ByName);
            Assert.False(byPlayers.IsChecked);
            Assert.Equal(new[] { "alpha", "Zeta" }, model.View.Items.Select(r => r.Name));

            Click(window, byPlayers);
            Assert.False(model.ByName);
            Assert.False(byName.IsChecked);
            Assert.Equal(new[] { "Zeta", "alpha" }, model.View.Items.Select(r => r.Name));
        }
        finally
        {
            if (window.IsVisible) window.Close();
            restore();
        }
    }

    [AvaloniaFact]
    public void Add_to_favorites_turns_into_disabled_in_favorites()
    {
        var model = Model();
        var row = Public(model, "Лесная деревня", "203.0.113.1", 10);
        model.All.Add(row);
        var (window, restore) = Show(model);
        try
        {
            var add = RowButton(window, row, model.AddToFavoritesCommand);
            Assert.True(add.IsEffectivelyVisible);
            Assert.True(add.IsEffectivelyEnabled);
            Assert.Equal(Loc.T("browse.addFavorite"), VisibleText(add));

            row.InFavorites = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(add.IsEffectivelyEnabled);
            Assert.Equal(Loc.T("browse.inFavorites"), VisibleText(add));
            // у строки общего списка «Изменить» и «Убрать» нет
            Assert.False(RowButton(window, row, model.EditFavoriteCommand).IsEffectivelyVisible);
            Assert.False(RowButton(window, row, model.RemoveFavoriteCommand).IsEffectivelyVisible);
        }
        finally
        {
            if (window.IsVisible) window.Close();
            restore();
        }
    }

    [AvaloniaFact]
    public void Window_closes_after_the_game_is_started()
    {
        // главная модель без конструктора: активного профиля нет — «сыграть» ничего не запускает, но окно закрывается
        var main = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        var model = Model(main);
        var favorite = new ServerRowViewModel(model, null, new PlayTarget("fav:Дом|192.168.1.20", PlayTargetKind.Favorite, "Дом", "192.168.1.20", null));
        model.Favorites.Add(favorite);
        model.IsFavoritesTab = true;
        var (window, restore) = Show(model);
        try
        {
            var play = RowButton(window, favorite, model.PlayCommand);
            Assert.True(play.IsEffectivelyVisible);
            // у избранного — «Изменить» и «Убрать» вместо «В избранное»
            Assert.False(RowButton(window, favorite, model.AddToFavoritesCommand).IsEffectivelyVisible);
            Assert.True(RowButton(window, favorite, model.EditFavoriteCommand).IsEffectivelyVisible);

            Click(window, play);

            Assert.False(window.IsVisible);
        }
        finally
        {
            if (window.IsVisible) window.Close();
            restore();
        }
    }

    /// <summary>Щелчок мышью по середине элемента — как человек: переключатели группы и кнопки отрабатывают сами.</summary>
    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Подпись кнопки: видимый TextBlock внутри (у «В избранное» их два, виден один).</summary>
    private static string? VisibleText(Button button) =>
        button.GetVisualDescendants().OfType<TextBlock>().Single(t => t.IsEffectivelyVisible).Text;
}

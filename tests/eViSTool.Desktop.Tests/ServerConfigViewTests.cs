using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Desktop.Platform;
using eViSTool.Desktop.Views;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Вкладка «Конфигурация» на настоящей модели и файле: редакторы подбираются по типу поля, ключ настройки мира выбран
/// в списке, переход к полю ставит в него курсор.
/// </summary>
public sealed class ServerConfigViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evistool-tests", "srvcfg-" + Guid.NewGuid().ToString("N"));

    public ServerConfigViewTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "serverconfig.json"), """
            {
              "ServerName": "Стенд",
              "WelcomeMessage": "Привет",
              "Port": 42420,
              "Upnp": false,
              "ConfigVersion": "1.10",
              "WorldConfig": { "WorldConfiguration": { "gameMode": "creative", "myModSetting": "42" } }
            }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>Лесная тема — на время теста: разметка вкладки берёт из неё шаблоны и иконки.</summary>
    private sealed class ForestTheme : IDisposable
    {
        private readonly List<ResourceInclude> _resources;

        public ForestTheme()
        {
            var baseUri = new Uri("avares://eViSTool.Desktop.Tests");
            _resources =
            [
                new ResourceInclude(baseUri) { Source = new Uri("avares://eViSTool/Themes/ForestResources.axaml") },
                new ResourceInclude(baseUri) { Source = new Uri("avares://eViSTool/Themes/ForestControls.axaml") },
            ];
            foreach (var include in _resources) Application.Current!.Resources.MergedDictionaries.Add(include);
        }

        public void Dispose()
        {
            foreach (var include in _resources) Application.Current!.Resources.MergedDictionaries.Remove(include);
        }
    }

    private (Window Window, ServerConfigViewModel Vm) Show()
    {
        if (!Ui.IsSet) Ui.Platform = new AvaloniaUiPlatform(); // таймер проверки файла — в потоке окна
        var vm = new ServerConfigViewModel();
        vm.OnProfileSwitched(_dir, "Стенд");
        vm.SetServerRunning(false);
        vm.SetActive(true);
        var window = new Window { Width = 1100, Height = 800, Content = new ServerConfigView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    /// <summary>Вкладку закрыли: проверка файла по таймеру останавливается.</summary>
    private static void Close(Window window, ServerConfigViewModel vm)
    {
        vm.SetActive(false);
        window.Close();
    }

    /// <summary>Видимый элемент по AutomationId; нет — в ошибке перечень видимых.</summary>
    private static T Find<T>(Visual root, string automationId) where T : Control
    {
        var visible = root.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && AutomationProperties.GetAutomationId(c) is not null).ToList();
        return visible.OfType<T>().FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == automationId)
               ?? throw new Xunit.Sdk.XunitException($"Нет видимого {typeof(T).Name} «{automationId}». Видны: "
                                                     + string.Join(", ", visible.Select(c => $"{c.GetType().Name} {AutomationProperties.GetAutomationId(c)}")));
    }

    [AvaloniaFact]
    public async Task Fields_get_editors_by_type()
    {
        using var theme = new ForestTheme();
        var (window, vm) = Show();
        Assert.Equal(ConfigLoadState.Loaded, vm.State);
        await Wait.Idle(100);

        Assert.Equal("Стенд", Find<TextBox>(window, "ServerName").Text);
        Assert.Equal(200, Find<TextBox>(window, "Port").Width); // число — узкое поле
        Assert.True(Find<TextBox>(window, "WelcomeMessage").AcceptsReturn); // несколько строк
        Assert.False(Find<CheckBox>(window, "Upnp").IsChecked); // переключатель

        vm.Tab = ConfigTab.Advanced;
        await Wait.Idle(100);
        Assert.Equal("1.10", Find<SelectableTextBlock>(window, "ConfigVersion").Text); // служебное — только посмотреть
        Close(window, vm);
    }

    [AvaloniaFact]
    public async Task World_setting_key_is_selected_in_list()
    {
        using var theme = new ForestTheme();
        var (window, vm) = Show();
        vm.Tab = ConfigTab.World;
        await Wait.Idle(100);

        var known = Find<ComboBox>(window, "WorldSettingKey.gameMode");
        Assert.Equal("gameMode", (known.SelectedItem as WorldSettingKeyOption)?.Key);
        Assert.Equal("gameMode", known.Text);
        Assert.Equal("creative", (Find<ComboBox>(window, "WorldSetting.gameMode").SelectedItem as WorldSettingValueOption)?.Value);

        var unknown = Find<ComboBox>(window, "WorldSettingKey.myModSetting"); // ключ от мода — в списке его нет
        Assert.Null(unknown.SelectedItem);
        Assert.Equal("myModSetting", unknown.Text);
        Assert.Equal("42", Find<TextBox>(window, "WorldSetting.myModSetting").Text);
        Assert.False(vm.IsDirty);
        Close(window, vm);
    }

    [AvaloniaFact]
    public async Task Focus_field_opens_its_tab_and_puts_caret_at_end()
    {
        using var theme = new ForestTheme();
        var (window, vm) = Show();
        vm.Tab = ConfigTab.Advanced;
        await Wait.Idle(100);

        vm.FocusField(ServerConfigViewModel.WelcomePath); // «Изменить» у приветствия во вкладке «Объявления»
        Assert.Equal(ConfigTab.General, vm.Tab);
        await Wait.Until(() => window.FocusManager?.GetFocusedElement() is TextBox box
                               && AutomationProperties.GetAutomationId(box) == ServerConfigViewModel.WelcomePath);

        var field = (TextBox)window.FocusManager!.GetFocusedElement()!;
        Assert.Equal("Привет".Length, field.CaretIndex);
        Close(window, vm);
    }
}

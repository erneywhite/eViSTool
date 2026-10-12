using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Desktop.Dialogs;
using eViSTool.Desktop.Platform;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Окно клона серверного профиля на настоящей модели: пары переключателей «мир» и «моды» на одном свойстве модели
/// (щелчок и правка из модели), Enter на переключателе — «Клонировать», готовый профиль закрывает окно, а «Отмена»,
/// крестик и Escape во время копирования только прерывают его.
/// </summary>
public sealed class CloneProfileWindowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evistool-tests", "clonewin-" + Guid.NewGuid().ToString("N"));
    private readonly GameProfile _source;

    public CloneProfileWindowTests()
    {
        var data = Directory.CreateDirectory(Path.Combine(_dir, "VintagestoryData")).FullName;
        Directory.CreateDirectory(Path.Combine(data, "ModConfig"));
        File.WriteAllText(Path.Combine(data, "ModConfig", "carryon.json"), "{}");
        File.WriteAllText(Path.Combine(data, "serverconfig.json"), """{ "WorldConfig": { "WorldName": "Дуо", "SaveFileLocation": "Saves/duo.vcdbs" } }""");
        _source = new GameProfile { Name = "Сервер", Kind = ProfileKind.Server, DataDir = data };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>Лесная тема — на время теста: окно берёт из неё иконку предупреждения и полоску хода.</summary>
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

    /// <summary>Копирование: подменённое (по умолчанию — «готово» сразу), журнал ошибок — мимо настоящего.</summary>
    private async Task<(CloneProfileWindow Window, CloneProfileViewModel Vm)> Open(
        Func<ClonePlan, IProgress<CloneProgress>, CancellationToken, Task<GameProfile>>? apply = null)
    {
        if (!Ui.IsSet) Ui.Platform = new AvaloniaUiPlatform(); // таймер паузы перед пересчётом — в потоке окна
        var services = CloneProfileServices.Default with
        {
            Apply = apply ?? ((plan, _, _) => Task.FromResult(new GameProfile { Name = plan.Options.Name })),
            LogError = _ => { },
        };
        var vm = new CloneProfileViewModel(_source, serverRunning: false, importName: null, services);
        var window = new CloneProfileWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Wait.Until(() => vm.CanGo);
        return (window, vm);
    }

    /// <summary>Переключатели по порядку: «Тот же мир», «Новый мир», «Общая папка модов», «Своя копия модов».</summary>
    private static List<RadioButton> Radios(Window window) => window.GetVisualDescendants().OfType<RadioButton>().ToList();

    private static void Click(Window window, Control control)
    {
        // окно меняет высоту по содержимому (появился ход копирования) — сначала разложить, потом искать точку
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var point = control.TranslatePoint(new Point(12, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static Button CancelButton(Window window) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Loc.T("common.cancel")));

    [AvaloniaFact]
    public async Task Radio_pairs_drive_the_model_and_follow_it()
    {
        using var theme = new ForestTheme();
        var (window, vm) = await Open();
        var radios = Radios(window);
        Assert.Equal(4, radios.Count);
        Assert.Equal([true, false, true, false], radios.Select(r => r.IsChecked == true));

        Click(window, radios[1]);
        Assert.True(vm.NewWorld);
        Assert.False(radios[0].IsChecked);
        Click(window, radios[0]);
        Assert.False(vm.NewWorld);
        Assert.False(radios[1].IsChecked);

        Click(window, radios[3]);
        Assert.False(vm.ShareMods);
        Assert.False(radios[2].IsChecked);
        Click(window, radios[2]);
        Assert.True(vm.ShareMods);
        Assert.False(radios[3].IsChecked);

        // и обратно: после щелчков переключатели по-прежнему следуют за моделью
        vm.NewWorld = true;
        vm.ShareMods = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([false, true, false, true], radios.Select(r => r.IsChecked == true));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Config_list_keeps_models_choice_and_passes_users_choice()
    {
        using var theme = new ForestTheme();
        // один конфиг: список скрыт, но выбор модели он не сбрасывает (иначе клон взял бы serverconfig.json вместо него)
        var (window, vm) = await Open();
        var combo = window.GetVisualDescendants().OfType<ComboBox>().Single();
        Assert.False(combo.IsEffectivelyVisible);
        Assert.Same(vm.Configs[0], vm.SelectedConfig);
        window.Close();

        File.WriteAllText(Path.Combine(_source.DataDir!, "serverconfig-b.json"), """{ "WorldConfig": { "WorldName": "Большой" } }""");
        (window, vm) = await Open();
        combo = window.GetVisualDescendants().OfType<ComboBox>().Single();
        Assert.True(combo.IsEffectivelyVisible);
        Assert.Same(vm.Configs[0], combo.SelectedItem);
        combo.SelectedIndex = 1;
        Assert.Same(vm.Configs[1], vm.SelectedConfig);
        Assert.False(vm.CanGo); // другой мир — план пересчитывается
        window.Close();
    }

    [AvaloniaFact]
    public async Task Enter_on_radio_clones_and_ready_profile_closes_window()
    {
        using var theme = new ForestTheme();
        var (window, vm) = await Open();
        var radio = Radios(window)[2];
        radio.Focus();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        await Wait.Until(() => !window.IsVisible);
        Assert.Equal(vm.Name, vm.Result?.Name);
        Assert.True(radio.IsChecked); // Enter не переключил сам переключатель
    }

    [AvaloniaFact]
    public async Task Cancel_and_closing_during_copy_only_stop_it()
    {
        using var theme = new ForestTheme();
        var (window, vm) = await Open(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("не отменили");
        });

        // «Отмена» во время копирования — прервать его, окно остаётся
        vm.GoCommand.Execute(null);
        await Wait.Until(() => vm.IsCopying);
        Click(window, CancelButton(window));
        await Wait.Until(() => !vm.IsCopying);
        Assert.True(window.IsVisible);
        Assert.Equal(Loc.T("clone.cancelled"), vm.ErrorText);

        // крестик (и Escape) во время копирования — тоже
        await Wait.Until(() => vm.CanGo);
        vm.GoCommand.Execute(null);
        await Wait.Until(() => vm.IsCopying);
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);
        await Wait.Until(() => !vm.IsCopying);

        // копирования нет — «Отмена» закрывает окно без ответа
        Click(window, CancelButton(window));
        Assert.False(window.IsVisible);
        Assert.Null(vm.Result);
    }

    [AvaloniaFact]
    public async Task Escape_closes_when_idle()
    {
        using var theme = new ForestTheme();
        var (window, _) = await Open();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(window.IsVisible);
    }
}

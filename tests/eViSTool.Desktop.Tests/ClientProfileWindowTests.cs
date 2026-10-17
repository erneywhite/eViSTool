using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Profiles;
using eViSTool.Desktop.Dialogs;
using eViSTool.Desktop.Platform;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Окно нового клиентского профиля с настоящей разметкой и лесной темой: что остаётся на виде — закрытие во время
/// копирования его прерывает, а окно остаётся; Enter на переключателе — «Создать», а не выбор варианта; переключатели
/// пишут в модель; готовый профиль закрывает окно. План и копирование подменены, диск не трогается (кроме пустой
/// временной папки основы).
/// </summary>
public sealed class ClientProfileWindowTests : IDisposable
{
    private readonly ResourceInclude _resources = new(new Uri("avares://eViSTool.Desktop.Tests"))
    {
        Source = new Uri("avares://eViSTool/Themes/ForestResources.axaml"),
    };

    private readonly ResourceInclude _controls = new(new Uri("avares://eViSTool.Desktop.Tests"))
    {
        Source = new Uri("avares://eViSTool/Themes/ForestControls.axaml"),
    };

    private readonly StyleInclude _styles = new(new Uri("avares://eViSTool.Desktop.Tests"))
    {
        Source = new Uri("avares://eViSTool/Themes/Forest.axaml"),
    };

    private readonly IUiPlatform? _oldPlatform = Ui.IsSet ? Ui.Platform : null;
    private readonly string _data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-tests", "cprofile-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly List<ClientCloneOptions> _planned = [];

    public ClientProfileWindowTests()
    {
        Ui.Platform = new AvaloniaUiPlatform(); // таймер паузы перед пересчётом плана
        var app = Application.Current!;
        app.Resources.MergedDictionaries.Add(_resources);
        app.Resources.MergedDictionaries.Add(_controls);
        app.Styles.Add(_styles);
    }

    public void Dispose()
    {
        var app = Application.Current!;
        app.Styles.Remove(_styles);
        app.Resources.MergedDictionaries.Remove(_controls);
        app.Resources.MergedDictionaries.Remove(_resources);
        if (_oldPlatform is not null) Ui.Platform = _oldPlatform;
        try { Directory.Delete(_data, recursive: true); } catch (IOException) { }
    }

    private (ClientProfileWindow Window, ClientProfileViewModel Model) Open(
        Func<ClientClonePlan, IProgress<CloneProgress>, CancellationToken, Task<GameProfile>> apply)
    {
        var source = new GameProfile { Name = "Основной", Kind = ProfileKind.Client, DataDir = _data };
        var services = new ClientProfileServices
        {
            Plan = (s, o) =>
            {
                lock (_planned) _planned.Add(o);
                return new ClientClonePlan
                {
                    Source = s,
                    Options = o,
                    Files = [new CloneFile("a", "a", 100)],
                    SettingsName = ClientProfileCloner.DefaultSettingsName,
                    ModDirs = [],
                };
            },
            IsGameRunning = _ => false,
            FreeSpace = _ => null,
            WorldBytes = _ => 0,
            Apply = apply,
        };
        var model = new ClientProfileViewModel(source, clone: false, "Клиент 2", services);
        var window = new ClientProfileWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, model);
    }

    private static T Named<T>(Window window, string automationId) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    /// <summary>Копирование, которое идёт, пока его не отменят.</summary>
    private static async Task<GameProfile> CopyUntilCancelled(TaskCompletionSource started, CancellationToken ct)
    {
        started.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        return new GameProfile();
    }

    [AvaloniaFact]
    public async Task Closing_during_copy_cancels_it_and_window_stays()
    {
        var started = new TaskCompletionSource();
        var (window, model) = Open((_, _, ct) => CopyUntilCancelled(started, ct));
        await Wait.Until(() => model.CanGo);

        model.GoCommand.Execute(null);
        await Wait.Until(() => started.Task.IsCompleted);
        Assert.False(Named<TextBox>(window, "CProfileName").IsEffectivelyEnabled); // форма выключена

        window.Close(); // крестик
        Assert.True(window.IsVisible);
        await Wait.Until(() => !model.IsCopying);
        Assert.True(window.IsVisible);
        Assert.True(Named<TextBox>(window, "CProfileName").IsEffectivelyEnabled);
        await Wait.Until(() => model.CanGo);

        // копирование не идёт — Escape закрывает без ответа
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(window.IsVisible);
        Assert.Null(model.Result);
    }

    [AvaloniaFact]
    public async Task Escape_during_copy_cancels_it_and_window_stays()
    {
        var started = new TaskCompletionSource();
        var (window, model) = Open((_, _, ct) => CopyUntilCancelled(started, ct));
        await Wait.Until(() => model.CanGo);

        model.GoCommand.Execute(null);
        await Wait.Until(() => started.Task.IsCompleted);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

        await Wait.Until(() => !model.IsCopying);
        Assert.True(window.IsVisible);
        window.Close();
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task Created_profile_closes_window()
    {
        var created = new GameProfile { Name = "Клиент 2", Kind = ProfileKind.Client };
        var (window, model) = Open((_, _, _) => Task.FromResult(created));
        await Wait.Until(() => model.CanGo);

        model.GoCommand.Execute(null);

        await Wait.Until(() => !window.IsVisible);
        Assert.Same(created, model.Result);
    }

    [AvaloniaFact]
    public async Task Enter_on_option_starts_copy_instead_of_choosing_it()
    {
        var applied = new TaskCompletionSource<ClientClonePlan>();
        var (window, model) = Open((plan, _, _) =>
        {
            applied.TrySetResult(plan);
            return Task.FromResult(new GameProfile());
        });
        await Wait.Until(() => model.CanGo);
        var copy = Named<RadioButton>(window, "CProfileModsCopy");
        copy.Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        await Wait.Until(() => applied.Task.IsCompleted);
        Assert.Equal(ClientModsMode.Shared, applied.Task.Result.Options.Mods);
        Assert.Equal(ClientModsMode.Shared, model.Mods);
    }

    [AvaloniaFact]
    public async Task Options_write_to_model_both_ways()
    {
        var (window, model) = Open((_, _, _) => Task.FromResult(new GameProfile()));
        await Wait.Until(() => model.CanGo);

        Mouse.Click(window, Named<RadioButton>(window, "CProfileModsCopy"));
        Assert.Equal(ClientModsMode.Copy, model.Mods);
        Assert.False(Named<RadioButton>(window, "CProfileModsShared").IsChecked);
        Mouse.Click(window, Named<RadioButton>(window, "CProfileModsEmpty"));
        Assert.Equal(ClientModsMode.Empty, model.Mods);

        Mouse.Click(window, Named<RadioButton>(window, "CProfileSettingsClean"));
        Assert.False(model.CopySettings);
        Assert.False(Named<RadioButton>(window, "CProfileSettingsCopy").IsChecked);
        Mouse.Click(window, Named<RadioButton>(window, "CProfileSettingsCopy"));
        Assert.True(model.CopySettings);
        Assert.False(Named<RadioButton>(window, "CProfileSettingsClean").IsChecked);
        Mouse.Click(window, Named<RadioButton>(window, "CProfileSettingsClean"));
        Assert.False(model.CopySettings); // переключается туда-обратно не один раз

        await Wait.Until(() => model.CanGo); // после паузы — пересчитанный план
        ClientCloneOptions last;
        lock (_planned) last = _planned[^1];
        Assert.Equal(ClientModsMode.Empty, last.Mods);
        Assert.False(last.CopySettings);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Typed_name_reaches_model_and_folder_follows()
    {
        var (window, model) = Open((_, _, _) => Task.FromResult(new GameProfile()));
        var name = Named<TextBox>(window, "CProfileName");
        var target = Named<TextBox>(window, "CProfileTarget");
        await Wait.Until(() => name.IsFocused); // фокус в названии при открытии, текст выделен
        Assert.Equal("Клиент 2".Length, name.SelectionEnd - name.SelectionStart);

        window.KeyTextInput("Мой");

        Assert.Equal("Мой", model.Name);
        Assert.Equal(Path.Combine(_data, ClientProfileLayout.ContainerName, "Мой"), target.Text);
        window.Close();
    }
}

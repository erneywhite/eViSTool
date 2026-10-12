using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Server;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Окно выбора папки на машине с сервером с настоящей разметкой и лесной темой: Enter в строке пути и в списке только
/// переходит (а не нажимает «Выбрать»), без отмеченной папки Enter — это «Выбрать», двойной щелчок входит в папку,
/// список дисков виден только когда дисков несколько, ответ закрывает окно. Папки подменены деревом в памяти.
/// </summary>
public sealed class RemoteFolderWindowTests : IDisposable
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

    public RemoteFolderWindowTests()
    {
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
    }

    /// <summary>Машина с сервером: Windows с двумя дисками, три папки на C:\Users и пустая C:\Users\erney; что у неё спрашивали — в <see cref="Asked"/>.</summary>
    private sealed class FakeData(string[] roots) : IServerData
    {
        public List<string?> Asked { get; } = [];
        public bool IsRemote => true;

        public Task<DirListing> ListDirsAsync(string? path, CancellationToken ct = default)
        {
            Asked.Add(path);
            var p = path is { Length: > 0 } ? path : roots[0];
            return Task.FromResult(p switch
            {
                "C:\\Users" => new DirListing(p, "C:\\", ["erney", "Public", "Shared"], roots),
                "C:\\Users\\erney" => new DirListing(p, "C:\\Users", [], roots),
                "D:\\" => new DirListing(p, null, ["Games"], roots),
                _ => new DirListing(roots[0], null, ["Users"], roots),
            });
        }

        public Task<ServerAutomation> LoadAutomationAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveAutomationAsync(ServerAutomation settings, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BackupEntry>> ListBackupsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BackupEntry> CopyWorldAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RestoreResult> RestoreAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteBackupAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> CheckBackupDirAsync(string? dir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StatsReport> LoadStatsAsync(StatsPeriod period, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetStatsEnabledAsync(bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearStatsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static async Task<(RemoteFolderWindow Window, RemoteFolderViewModel Model, FakeData Data)> Open(string start = "C:\\Users", params string[] roots)
    {
        var data = new FakeData(roots.Length > 0 ? roots : ["C:\\", "D:\\"]);
        var model = new RemoteFolderViewModel(data, start, "Папка для копий мира");
        var window = new RemoteFolderWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Wait.Until(() => model.Dirs.Count > 0 || model.HasError);
        Dispatcher.UIThread.RunJobs();
        return (window, model, data);
    }

    private static T Named<T>(Window window, string automationId) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    private static void Enter(Window window) => window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

    private static void DoubleClick(Window window, Visual visual)
    {
        var point = visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), window)!.Value;
        for (var i = 0; i < 2; i++)
        {
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Opens_ListingTheStartFolder()
    {
        var (window, model, data) = await Open();

        Assert.Equal(["C:\\Users"], data.Asked);
        Assert.Equal("C:\\Users", Named<TextBox>(window, "RemoteFolderPath").Text);
        var list = Named<ListBox>(window, "RemoteFolderDirs");
        Assert.Equal(["erney", "Public", "Shared"], list.Items.Cast<string>());
        Assert.True(Named<Button>(window, "RemoteFolderUp").IsEffectivelyEnabled);
        Assert.False(model.HasError);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DriveList_IsShownOnlyWithSeveralDrives()
    {
        var (window, _, _) = await Open();
        var roots = Named<ComboBox>(window, "RemoteFolderRoots");
        Assert.True(roots.IsEffectivelyVisible);
        Assert.Equal("C:\\", roots.SelectedItem);
        window.Close();

        (window, _, _) = await Open("C:\\Users", "C:\\");
        Assert.False(Named<ComboBox>(window, "RemoteFolderRoots").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task PickingADrive_InTheList_OpensItsRoot()
    {
        var (window, model, data) = await Open();

        Named<ComboBox>(window, "RemoteFolderRoots").SelectedItem = "D:\\";
        await model.Navigation;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["C:\\Users", "D:\\"], data.Asked);
        Assert.Equal("D:\\", Named<TextBox>(window, "RemoteFolderPath").Text);
        Assert.Equal(["Games"], Named<ListBox>(window, "RemoteFolderDirs").Items.Cast<string>());
        Assert.Equal("D:\\", Named<ComboBox>(window, "RemoteFolderRoots").SelectedItem);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Enter_InThePathBox_GoesToThePath_AndDoesNotChoose()
    {
        var (window, model, data) = await Open();
        var box = Named<TextBox>(window, "RemoteFolderPath");
        box.Focus();
        box.Text = "C:\\Users\\erney";

        Enter(window);
        await model.Navigation;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["C:\\Users", "C:\\Users\\erney"], data.Asked);
        Assert.Equal("C:\\Users\\erney", model.PathText);
        Assert.True(window.IsVisible); // «Выбрать» не нажалась
        Assert.Null(model.Selected);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Enter_OnAMarkedFolder_EntersIt_AndDoesNotChoose()
    {
        var (window, model, data) = await Open();
        var list = Named<ListBox>(window, "RemoteFolderDirs");
        list.SelectedIndex = 0;
        list.ContainerFromIndex(0)!.Focus();

        Enter(window);
        await model.Navigation;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["C:\\Users", "C:\\Users\\erney"], data.Asked);
        Assert.Equal("C:\\Users\\erney", model.PathText);
        Assert.True(window.IsVisible);
        Assert.Null(model.Selected);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Enter_WithNothingMarked_ChoosesTheOpenFolder()
    {
        var (window, model, data) = await Open();
        Named<ListBox>(window, "RemoteFolderDirs").Focus();

        Enter(window);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("C:\\Users", model.Selected);
        Assert.False(window.IsVisible);
        Assert.Equal(["C:\\Users"], data.Asked);
    }

    [AvaloniaFact]
    public async Task Choose_OnAMarkedFolder_GivesItsPath_AndClosesTheWindow()
    {
        var (window, model, _) = await Open();
        Named<ListBox>(window, "RemoteFolderDirs").SelectedIndex = 1;
        var chosen = new List<string>();
        model.Chosen += chosen.Add;

        Named<Button>(window, "RemoteFolderChoose").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["C:\\Users\\Public"], chosen);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task DoubleClick_OnAFolder_EntersIt()
    {
        var (window, model, data) = await Open();
        var list = Named<ListBox>(window, "RemoteFolderDirs");

        DoubleClick(window, list.ContainerFromIndex(0)!);
        await model.Navigation;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["C:\\Users", "C:\\Users\\erney"], data.Asked);
        Assert.Equal("C:\\Users\\erney", Named<TextBox>(window, "RemoteFolderPath").Text);
        Assert.True(window.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Up_GoesToTheParent_AndIsDisabledAtTheTop()
    {
        var (window, model, _) = await Open();
        var up = Named<Button>(window, "RemoteFolderUp");

        up.Command!.Execute(null);
        await model.Navigation;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("C:\\", model.PathText);
        Assert.False(up.IsEffectivelyEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ErrorLine_IsShownOnlyWithAnError()
    {
        var (window, model, _) = await Open();
        var error = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("error"));
        Assert.False(error.IsEffectivelyVisible);

        // такого пути там нет — машина показывает ближайшую папку выше, и окно говорит об этом
        model.PathText = "C:\\Nowhere";
        await model.GoToPathCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(error.IsEffectivelyVisible);
        Assert.Equal(model.Error, error.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Escape_AndCancel_CloseWithoutAnswer()
    {
        var (window, model, _) = await Open();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
        Assert.Null(model.Selected);

        (window, model, _) = await Open();
        var cancel = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsCancel);
        cancel.Command?.Execute(null);
        cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
        Assert.Null(model.Selected);
    }
}

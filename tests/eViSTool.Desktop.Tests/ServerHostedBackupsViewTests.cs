using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Tests.Hosting;
using eViSTool.Desktop.Platform;
using eViSTool.Desktop.Views;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Вкладка «Копии» раздела «Сервер» у хостера: список копий с метками и действиями, пустой список, строка хода с
/// полосой и отменой, пока идёт скачивание, итог, переключатели расписания и поле «Хранить последних». Сервер —
/// поддельный FTP; копии в папке — файлы с именами, как их делает eViSTool.
/// </summary>
public sealed class ServerHostedBackupsViewTests : IDisposable
{
    private readonly HostedFixture _host = new();
    private readonly string _copies;

    public ServerHostedBackupsViewTests()
    {
        if (!Ui.IsSet) Ui.Platform = new AvaloniaUiPlatform();
        _host.GPortalLayout(config: false, log: false); // мир — единственный в Saves
        _copies = Directory.CreateDirectory(Path.Combine(_host.Dir, "copies")).FullName;
        _host.Settings.BackupDir = _copies;
    }

    public void Dispose() => _host.Dispose();

    /// <summary>Копия в папке: файл мира, по желанию — архив настроек и сведения «откуда».</summary>
    private void PutCopy(string name, bool own)
    {
        var path = Path.Combine(_copies, name);
        File.WriteAllBytes(path, new byte[4096]);
        if (!own) return;
        File.WriteAllBytes(HostedWorldBackups.ExtraFor(path), new byte[1024]);
        File.WriteAllText(HostedWorldBackups.InfoFor(path),
            $$"""{ "ProfileId": "{{_host.Profile.Id}}", "Origin": "GameBackup", "Source": "/Backups/genbackup.vcdbs" }""");
    }

    private async Task<HostedBackupsViewModel> OpenAsync(ProfileViewModel? profile = null)
    {
        var model = new HostedBackupsViewModel(null, new HostedWorldJobs());
        model.OnProfileSwitched(profile ?? new ProfileViewModel(_host.Profile, _ => { }));
        model.SetActive(true);
        var refresh = model.RefreshAsync();
        await Wait.Until(() => refresh.IsCompleted);
        return model;
    }

    /// <summary>Вид в окне с лесной темой (на уровне окна, чтобы не менять приложение тестов для остальных проверок).</summary>
    private static Window Show(HostedBackupsViewModel model)
    {
        var baseUri = new Uri("avares://eViSTool.Desktop.Tests");
        var window = new Window { Width = 1200, Height = 900, RequestedThemeVariant = ThemeVariant.Dark, Content = new ServerHostedBackupsView { DataContext = model } };
        window.Resources.MergedDictionaries.Add(new ResourceInclude(baseUri) { Source = new Uri("avares://eViSTool/Themes/ForestResources.axaml") });
        window.Resources.MergedDictionaries.Add(new ResourceInclude(baseUri) { Source = new Uri("avares://eViSTool/Themes/ForestControls.axaml") });
        window.Styles.Add(new StyleInclude(baseUri) { Source = new Uri("avares://eViSTool/Themes/Forest.axaml") });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Named<T>(Control root, string automationId) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    private static List<T> AllNamed<T>(Control root, string automationId) where T : Control =>
        [.. root.GetVisualDescendants().OfType<T>().Where(c => AutomationProperties.GetAutomationId(c) == automationId)];

    [AvaloniaFact]
    public async Task Copies_are_listed_with_their_badges_and_actions()
    {
        PutCopy("Hosted-2026-10-17_09-00-00.vcdbs", own: true);
        PutCopy("old-world.vcdbs", own: false);
        var model = await OpenAsync();
        var window = Show(model);

        var list = Named<ItemsControl>(window, "HostedBackupsList");
        Assert.Equal(2, list.ItemCount);
        var texts = list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Hosted-2026-10-17_09-00-00.vcdbs", texts);
        Assert.Contains("old-world.vcdbs", texts);
        Assert.Contains(Loc.T("hostedbk.originGame"), texts);     // своя — «копия игры»
        Assert.Contains(Loc.T("hostedbk.originUnknown"), texts);  // положенная руками — откуда, неизвестно
        Assert.Contains(Loc.T("hostedbk.extra"), texts);
        Assert.Contains(Loc.T("sched.manual"), texts);            // чужую ротация не трогает
        Assert.Equal(2, AllNamed<Button>(window, "HostedBackupRestore").Count(b => b.IsEffectivelyEnabled));
        Assert.Equal(2, AllNamed<Button>(window, "HostedBackupShow").Count);
        Assert.Equal(2, AllNamed<Button>(window, "HostedBackupDelete").Count);
        Assert.False(Named<TextBlock>(window, "HostedBackupsEmpty").IsEffectivelyVisible);
        Assert.False(Named<DockPanel>(window, "HostedBackupWork").IsVisible);
        Assert.False(Named<TextBlock>(window, "HostedBackupStatus").IsVisible);
        Assert.False(Named<TextBlock>(window, "HostedBackupError").IsVisible);
        Assert.True(Named<TextBlock>(window, "HostedBackupsTotal").IsEffectivelyVisible);
        Assert.True(Named<Button>(window, "HostedBackupNow").IsEffectivelyEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task While_downloading_the_work_line_shows_progress_and_cancel_then_the_outcome()
    {
        _host.Server.Put("/Logs/server-main.log", HostedLogSamples.Bytes(HostedLogSamples.Stopped));
        _host.Server.Put("/Saves/MyVintageStoryWorld.vcdbs", new byte[64 * 1024]);
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _host.Server.OnBefore = (op, path) =>
        {
            if (op != "download" || path != "/Saves/MyVintageStoryWorld.vcdbs") return;
            reached.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        };
        var model = await OpenAsync();
        var window = Show(model);
        Assert.True(Named<TextBlock>(window, "HostedBackupsEmpty").IsEffectivelyVisible);

        Mouse.Click(window, Named<Button>(window, "HostedBackupNow"));
        await Wait.Until(() => reached.IsSet);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Named<DockPanel>(window, "HostedBackupWork").IsEffectivelyVisible);
        Assert.True(Named<ProgressBar>(window, "HostedBackupProgress").IsEffectivelyVisible);
        Assert.NotEqual("", Named<TextBlock>(window, "HostedBackupWorkText").Text);
        Assert.False(Named<Button>(window, "HostedBackupNow").IsEffectivelyEnabled); // вторая копия не начнётся
        var cancel = Named<Button>(window, "HostedBackupCancel");
        Assert.True(cancel.IsEffectivelyVisible);
        Mouse.Click(window, cancel);
        release.Set();
        await Wait.Until(() => !model.IsWorking);
        Dispatcher.UIThread.RunJobs();

        Assert.False(Named<DockPanel>(window, "HostedBackupWork").IsVisible);
        var status = Named<TextBlock>(window, "HostedBackupStatus");
        Assert.True(status.IsEffectivelyVisible);
        Assert.Equal(Loc.T("hostedbk.cancelled"), status.Text);
        Assert.True(Named<Button>(window, "HostedBackupNow").IsEffectivelyEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Schedule_chips_and_the_keep_field_write_to_the_profile()
    {
        var saved = 0;
        var model = await OpenAsync(new ProfileViewModel(_host.Profile, _ => { }, _ => saved++));
        var window = Show(model);
        Assert.True(Named<RadioButton>(window, "HostedBackupScheduleOff").IsChecked);

        Mouse.Click(window, Named<RadioButton>(window, "HostedBackupScheduleDaily"));

        Assert.Equal(HostedBackupSchedule.Daily, model.Schedule);
        Assert.Equal(HostedBackupSchedule.Daily, _host.Settings.BackupSchedule);
        Assert.Equal(1, saved);

        Named<TextBox>(window, "HostedBackupKeep").Text = "не число";
        Dispatcher.UIThread.RunJobs();
        var error = Named<TextBlock>(window, "HostedBackupKeepError");
        Assert.True(error.IsEffectivelyVisible);
        Assert.Equal(Loc.T("sched.keepError"), error.Text);
        Assert.Equal(7, _host.Settings.BackupKeep);
        window.Close();
    }
}

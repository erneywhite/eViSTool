using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Server;
using eViSTool.Desktop.Platform;
using eViSTool.Desktop.Views;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Вкладка «Расписание»: поле папки для копий применяется, когда поле отпустили (привязка LostFocus) или нажали
/// Enter (код вкладки отдаёт текст модели); чипы режима перезапуска, строки списка копий. Вкладка берёт лесную тему
/// окна, поэтому она подключена к окну теста.
/// </summary>
public sealed class ServerScheduleViewTests
{
    private static (Window Window, ServerScheduleView View, ServerScheduleViewModel Model) Open()
    {
        // модели раздела берут таймеры и вопросы у платформы окна; главная модель вкладке не нужна
        if (!Ui.IsSet) Ui.Platform = new AvaloniaUiPlatform();
        var model = new ServerViewModel(null!).Schedule;
        var window = new Window { Width = 1000, Height = 900, RequestedThemeVariant = ThemeVariant.Dark };
        var host = new Uri("avares://eViSTool.Desktop.Tests");
        window.Resources.MergedDictionaries.Add(new ResourceInclude(host) { Source = new Uri("avares://eViSTool.Desktop/Themes/ForestResources.axaml") });
        window.Resources.MergedDictionaries.Add(new ResourceInclude(host) { Source = new Uri("avares://eViSTool.Desktop/Themes/ForestControls.axaml") });
        window.Styles.Add(new StyleInclude(host) { Source = new Uri("avares://eViSTool.Desktop/Themes/Forest.axaml") });
        var view = new ServerScheduleView { DataContext = model };
        window.Content = view;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, model);
    }

    private static T Named<T>(Control root, string automationId) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    [AvaloniaFact]
    public void Enter_in_folder_field_applies_the_text_without_leaving_the_field()
    {
        var (window, view, model) = Open();
        var box = Named<TextBox>(view, "SchedBackupDir");
        box.Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyTextInput(@"D:\copies");
        Assert.Equal("", model.BackupDirText); // привязка LostFocus: пока поле держит фокус, модель текста не знает

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(@"D:\copies", model.BackupDirText);
        Assert.True(box.IsFocused); // Enter поле не отпускает
        window.Close();
    }

    [AvaloniaFact]
    public void Leaving_the_folder_field_applies_the_text()
    {
        var (window, view, model) = Open();
        var box = Named<TextBox>(view, "SchedBackupDir");
        box.Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput(@"D:\copies");

        Named<TextBox>(view, "SchedKeep").Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(@"D:\copies", model.BackupDirText);
        window.Close();
    }

    [AvaloniaFact]
    public void Other_fields_apply_on_every_key()
    {
        var (window, view, model) = Open();
        Named<TextBox>(view, "SchedKeep").Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyTextInput("5");

        Assert.Contains("5", model.KeepText); // сразу, без ухода из поля
        Assert.NotEqual("7", model.KeepText);
        window.Close();
    }

    [AvaloniaFact]
    public void Empty_folder_field_shows_default_folder_as_placeholder()
    {
        var (window, view, model) = Open();
        var box = Named<TextBox>(view, "SchedBackupDir");

        Assert.Equal("", box.Text ?? "");
        Assert.Equal(model.BackupDirDefault, box.PlaceholderText);
        var placeholder = box.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Placeholder");
        Assert.True(placeholder.IsVisible);
        Assert.Equal(12, placeholder.FontSize);
        Assert.Equal(TextTrimming.CharacterEllipsis, placeholder.TextTrimming);
        window.Close();
    }

    [AvaloniaFact]
    public void Restart_chips_switch_the_mode_and_show_its_rows()
    {
        var (window, view, model) = Open();
        var hours = Named<TextBox>(view, "SchedRestartHours");
        var times = Named<TextBox>(view, "SchedRestartTimes");
        var warn = Named<TextBox>(view, "SchedRestartWarn");
        Assert.Equal(RestartMode.Off, model.RestartMode);
        Assert.True(Named<RadioButton>(view, "SchedRestartOff").IsChecked);
        Assert.False(hours.IsEffectivelyVisible || times.IsEffectivelyVisible || warn.IsEffectivelyVisible);

        Named<RadioButton>(view, "SchedRestartDaily").IsChecked = true; // как щелчок по чипу
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(RestartMode.Daily, model.RestartMode);
        Assert.True(times.IsEffectivelyVisible);
        Assert.True(warn.IsEffectivelyVisible);
        Assert.False(hours.IsEffectivelyVisible);

        model.RestartMode = RestartMode.Interval; // и обратно: модель → чипы
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<RadioButton>(view, "SchedRestartInterval").IsChecked);
        Assert.False(Named<RadioButton>(view, "SchedRestartDaily").IsChecked);
        Assert.True(hours.IsEffectivelyVisible);
        Assert.False(times.IsEffectivelyVisible);
        Assert.True(warn.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Backup_rows_take_their_commands_from_the_tab_and_show_their_badges()
    {
        var (window, view, model) = Open();
        var foreign = new BackupRowViewModel(new BackupEntry("default-2026-09-30_17-51-32.vcdbs", new DateTime(2026, 9, 30, 17, 51, 32), 2_100_000, IsOwn: false)
            { ModDataSize = 400_000 });
        var own = new BackupRowViewModel(new BackupEntry("stand-2026-10-12_09-00-00.vcdbs", new DateTime(2026, 10, 12, 9, 0, 0), 3_600_000, IsOwn: true)
            { LocalPath = @"C:\x\stand-2026-10-12_09-00-00.vcdbs" });
        model.Backups.Add(foreign);
        model.Backups.Add(own);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var buttons = view.GetVisualDescendants().OfType<Button>().ToList();
        // команды строк — команды вкладки, параметр — строка списка
        Assert.Equal([foreign, own], buttons.Where(b => b.Command == model.DeleteCommand).Select(b => b.CommandParameter));
        Assert.Equal([foreign, own], buttons.Where(b => b.Command == model.RestoreCommand).Select(b => b.CommandParameter));
        // «Показать» — только у копии на этой машине
        Assert.Equal([own], buttons.Where(b => b.Command == model.ShowInFolderCommand && b.IsEffectivelyVisible).Select(b => b.CommandParameter));
        // метки: у чужой копии «не ротируется» и «данные модов», у своей без архива — ни одной
        var badges = view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("badge") && b.IsEffectivelyVisible).ToList();
        Assert.Equal(2, badges.Count);
        Assert.All(badges, b => Assert.Same(foreign, b.DataContext));
        window.Close();
    }
}

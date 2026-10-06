using System.Windows;
using eViSTool.Core.Diagnostics;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.App;

/// <summary>Строка отчёта: мод, сколько ошибок, пример; можно ли выключить (мод нашёлся среди установленных).</summary>
public sealed class ModErrorRow(ModErrorLine line)
{
    public ModErrorLine Line { get; } = line;
    public string CountText { get; } = Loc.T("errs.count", line.Count);
    public bool CanDisable { get; } = line.Path is not null;
}

/// <summary>
/// «Ошибки модов» за последний запуск игры или сервера профиля — по кнопке «!» у профиля. Игра их проглотила и
/// работает, но они копятся (и бывают фризы): какие моды шумят, пример ошибки, выключить, страница мода.
/// </summary>
public partial class ModErrorsWindow : Window
{
    private readonly ViewModels.MainViewModel _main;
    private readonly GameProfile _profile;
    private readonly ModErrorReport _report;

    public ModErrorsWindow(ViewModels.MainViewModel main, GameProfile profile, ModErrorReport report)
    {
        InitializeComponent();
        (_main, _profile, _report) = (main, profile, report);
        Summary.Text = Loc.T("errs.summary", profile.Name, report.At.ToString("dd.MM HH:mm"), report.Total, report.Mods.Count)
                       + (report.Unattributed > 0 ? " " + Loc.T("errs.unattributed", report.Unattributed) : "");
        Rows.ItemsSource = report.Mods.Select(l => new ModErrorRow(l)).ToList();
    }

    private async void Disable_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModErrorRow { Line.Path: { } path } row || ModTarget.For(_profile) is not { } target) return;
        ((UIElement)sender).IsEnabled = false;
        try
        {
            var mod = target.IsRemote ? new LocalMod(path, null, null) : ModScanner.ReadZip(path);
            if (_profile.Kind == ProfileKind.Client && _main.IsGameRunningFor(_profile))
            {
                // игра открыта — при выходе она перезапишет список выключенных модов: выключаем после
                _main.RunAfterGameExit(_profile, () => ModTargets.SetEnabledAsync(target, mod, enabled: false));
                Status.Text = Loc.T("crash.disableAfterExit", row.Line.Name);
                return;
            }
            await ModTargets.SetEnabledAsync(target, mod, enabled: false);
            if (_main.ActiveProfile?.Model.Id == _profile.Id) _main.Mods.ReloadLocal();
            Status.Text = Loc.T(_profile.Kind == ProfileKind.Server ? "crash.disabledServer" : "crash.disabled", row.Line.Name);
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or UnauthorizedAccessException
                                       or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            Status.Text = Loc.T("crash.disableFailed", ex.Message);
            ((UIElement)sender).IsEnabled = true;
        }
    }

    private void Catalog_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModErrorRow row) return;
        _ = _main.ShowInCatalogAsync(null, row.Line.ModId, row.Line.Name);
        Close();
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        _main.DismissErrorReport(_profile, _report);
        Close();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = Summary.Text + Environment.NewLine + string.Join(Environment.NewLine,
            _report.Mods.Select(m => $"{m.Name} {m.Version} — {m.Count}: {m.Example}"));
        try { Clipboard.SetText(text); Status.Text = Loc.T("crash.copied"); }
        catch (System.Runtime.InteropServices.COMException) { }
    }
}

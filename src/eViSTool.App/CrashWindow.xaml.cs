using System.Windows;
using eViSTool.Core.Diagnostics;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.App;

/// <summary>
/// «Игра вылетела» / «Мир закрылся из-за ошибки»: какой мод, похоже, виноват и почему мы так думаем, и что с ним
/// сделать — выключить, найти в «Моих модах», открыть его страницу. Подробности (ошибка и стек) — для автора мода.
/// </summary>
public partial class CrashWindow : Window
{
    private readonly CrashFinding _finding;
    private readonly GameProfile _profile;
    private readonly ViewModels.MainViewModel _main;

    public CrashWindow(ViewModels.MainViewModel main, GameProfile profile, CrashFinding finding)
    {
        InitializeComponent();
        (_main, _profile, _finding) = (main, profile, finding);
        Title = Heading.Text = Loc.T(finding.Kind == GameExitKind.Crashed ? "crash.titleCrashed" : "crash.titleWorld");
        Profile.Text = Loc.T("crash.profile", profile.Name);

        if (finding.Culprit is { } c)
        {
            var name = c.Mod?.Name ?? c.ModId;
            Culprit.Text = Loc.T("crash.culprit", name, c.Version ?? "");
            How.Text = c.Source switch
            {
                CulpritSource.GameReport => Loc.T("crash.howGameReport"),
                CulpritSource.Stack => Loc.T("crash.howStack"),
                CulpritSource.Message => Loc.T("crash.howMessage"),
                _ => Loc.T("crash.howTranslation"),
            } + (c.Mod is null ? " " + Loc.T("crash.notInstalled") : "");
        }
        else
        {
            Culprit.Text = Loc.T("crash.unknown");
            How.Text = Loc.T("crash.unknownHint");
        }
        Reason.Text = Loc.T("crash.reason", finding.Reason.TrimEnd('.', ' '))
                      + (finding.ErrorCount > 1 ? " " + Loc.T("crash.errorCount", finding.ErrorCount) : "");
        Details.Text = string.Join(Environment.NewLine, new[] { finding.Error ?? "" }.Concat(finding.Stack.Select(f => "   at " + f)));

        var installed = finding.Culprit?.Mod is not null;
        DisableButton.Visibility = ShowButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        CatalogButton.Visibility = finding.Culprit is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Disable_Click(object sender, RoutedEventArgs e)
    {
        if (_finding.Culprit?.Mod is not { } mod || ModTarget.For(_profile) is not { } target) return;
        DisableButton.IsEnabled = false;
        if (_main.IsGameRunningFor(_profile))
        {
            // мир закрылся, а игра открыта: при выходе она перезапишет список выключенных модов — выключаем после
            _main.RunAfterGameExit(_profile, () => ModTargets.SetEnabledAsync(target, ModScanner.ReadZip(mod.Path), enabled: false));
            Status.Text = Loc.T("crash.disableAfterExit", mod.Name);
            return;
        }
        try
        {
            // игра в этот момент могла ещё работать (мир закрылся, а сама она открыта) — выключение применится при следующем входе
            await ModTargets.SetEnabledAsync(target, ModScanner.ReadZip(mod.Path), enabled: false);
            Status.Text = Loc.T("crash.disabled", mod.Name);
            if (_main.ActiveProfile?.Model.Id == _profile.Id) _main.Mods.ReloadLocal();
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Status.Text = Loc.T("crash.disableFailed", ex.Message);
            DisableButton.IsEnabled = true;
        }
    }

    private void Show_Click(object sender, RoutedEventArgs e)
    {
        if (_finding.Culprit is not { } c) return;
        var profile = _main.Profiles.FirstOrDefault(p => p.Model.Id == _profile.Id);
        if (profile is not null && _main.ActiveProfile != profile) _main.ActiveProfile = profile;
        _main.SelectedTab = 0;
        _main.Mods.Search = c.ModId;
        Application.Current.MainWindow?.Activate();
    }

    private void Catalog_Click(object sender, RoutedEventArgs e)
    {
        if (_finding.Culprit is not { } c) return;
        _ = _main.ShowInCatalogAsync(null, c.ModId, c.Mod?.Name);
        Application.Current.MainWindow?.Activate();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = $"{Heading.Text} — {Profile.Text}{Environment.NewLine}{Culprit.Text}{Environment.NewLine}{Reason.Text}{Environment.NewLine}{Environment.NewLine}{Details.Text}";
        try { Clipboard.SetText(text); Status.Text = Loc.T("crash.copied"); }
        catch (System.Runtime.InteropServices.COMException) { } // буфер занят другой программой
    }
}

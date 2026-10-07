using System.Windows;
using eViSTool.Core.Diagnostics;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;

namespace eViSTool.App;

/// <summary>Что показать в окне вылета — для игры (разбор её логов) и для сервера (разбор агента).</summary>
public sealed record CrashView(string Title, string ProfileName, string? ModId, string? ModName, string? ModVersion, CulpritSource? Source,
    bool ModInstalled, string Reason, int ErrorCount, string? Error, IReadOnlyList<string> Stack)
{
    public static CrashView ForGame(GameProfile profile, CrashFinding f) =>
        new(Loc.T(f.Kind == GameExitKind.Crashed ? "crash.titleCrashed" : "crash.titleWorld"), profile.Name,
            f.Culprit?.ModId, f.Culprit?.Mod?.Name ?? f.Culprit?.ModId, f.Culprit?.Version, f.Culprit?.Source, f.Culprit?.Mod is not null,
            f.Reason, f.ErrorCount, f.Error, f.Stack);

    public static CrashView ForServer(GameProfile profile, ServerCrashInfo c) =>
        new(Loc.T("crash.titleServer", profile.Name), profile.Name, c.ModId, c.ModName, c.ModVersion, c.Source, c.ModPath is not null,
            c.Reason, c.ErrorCount, c.Error, c.Stack);
}

/// <summary>
/// «Игра вылетела» / «Мир закрылся из-за ошибки» / «Сервер упал»: какой мод, похоже, виноват и почему мы так думаем,
/// и что с ним сделать — выключить, найти, открыть его страницу. Подробности (ошибка и стек) — для автора мода.
/// </summary>
public partial class CrashWindow : Window
{
    private readonly CrashView _view;
    private readonly ViewModels.MainViewModel _main;
    private readonly Func<Task<string>>? _disable;
    private readonly Action _show;

    /// <param name="disable">Выключить мод-виновник; возвращает, что сказать (null — выключать нечего).</param>
    /// <param name="show">Перейти к моду или серверу в программе.</param>
    public CrashWindow(ViewModels.MainViewModel main, CrashView view, Func<Task<string>>? disable, Action show, string showLabel)
    {
        InitializeComponent();
        (_main, _view, _disable, _show) = (main, view, disable, show);
        Title = Heading.Text = view.Title;
        Profile.Text = Loc.T("crash.profile", view.ProfileName);
        ShowButton.Content = showLabel;

        if (view.ModId is not null)
        {
            Culprit.Text = Loc.T("crash.culprit", view.ModName ?? view.ModId, view.ModVersion ?? "");
            How.Text = view.Source switch
            {
                CulpritSource.GameReport => Loc.T("crash.howGameReport"),
                CulpritSource.Stack => Loc.T("crash.howStack"),
                CulpritSource.Message => Loc.T("crash.howMessage"),
                _ => Loc.T("crash.howTranslation"),
            } + (view.ModInstalled ? "" : " " + Loc.T("crash.notInstalled"));
        }
        else
        {
            Culprit.Text = Loc.T("crash.unknown");
            How.Text = Loc.T("crash.unknownHint");
        }
        Reason.Text = Loc.T("crash.reason", view.Reason.TrimEnd('.', ' '))
                      + (view.ErrorCount > 1 ? " " + Loc.T("crash.errorCount", view.ErrorCount) : "");
        Details.Text = string.Join(Environment.NewLine, new[] { view.Error ?? "" }.Concat(view.Stack.Select(f => "   at " + f)));

        DisableButton.Visibility = disable is not null && view.ModInstalled ? Visibility.Visible : Visibility.Collapsed;
        CatalogButton.Visibility = view.ModId is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Окно вылета игры профиля: выключение мода — сразу, а пока игра открыта — после её закрытия.</summary>
    public static CrashWindow ForGame(ViewModels.MainViewModel main, GameProfile profile, CrashFinding finding)
    {
        Func<Task<string>>? disable = null;
        if (finding.Culprit?.Mod is { } mod && ModTarget.For(profile) is { } target)
            disable = async () =>
            {
                if (main.IsGameRunningFor(profile))
                {
                    // мир закрылся, а игра открыта: при выходе она перезапишет список выключенных модов — выключаем после
                    main.RunAfterGameExit(profile, () => ModTargets.SetEnabledAsync(target, ModScanner.ReadZip(mod.Path), enabled: false));
                    return Loc.T("crash.disableAfterExit", mod.Name);
                }
                await ModTargets.SetEnabledAsync(target, ModScanner.ReadZip(mod.Path), enabled: false);
                if (main.ActiveProfile?.Model.Id == profile.Id) main.Mods.ReloadLocal();
                return Loc.T("crash.disabled", mod.Name);
            };
        return new CrashWindow(main, CrashView.ForGame(profile, finding), disable, () =>
        {
            main.SwitchTo(profile, tab: ViewModels.AppTab.Mods);
            if (finding.Culprit is { } c) main.Mods.Search = c.ModId;
        }, Loc.T("crash.showInMods"));
    }

    /// <summary>Окно падения сервера: мод выключается в конфиге сервера (свой — на диске, удалённый — через агента).</summary>
    public static CrashWindow ForServer(ViewModels.MainViewModel main, GameProfile profile, ServerCrashInfo crash)
    {
        Func<Task<string>>? disable = null;
        if (crash.ModPath is { } path && ModTarget.For(profile) is { } target)
            disable = async () =>
            {
                // путь — на машине сервера; у своего сервера читаем мод с диска, удалённому хватит пути
                var mod = target.IsRemote ? new LocalMod(path, null, null) : ModScanner.ReadZip(path);
                await ModTargets.SetEnabledAsync(target, mod, enabled: false);
                if (main.ActiveProfile?.Model.Id == profile.Id) main.Mods.ReloadLocal();
                return Loc.T("crash.disabledServer", crash.ModName ?? crash.ModId);
            };
        return new CrashWindow(main, CrashView.ForServer(profile, crash), disable, () => main.SwitchTo(profile, tab: ViewModels.AppTab.Server),
            Loc.T("crash.showServer"));
    }

    private async void Disable_Click(object sender, RoutedEventArgs e)
    {
        if (_disable is null) return;
        DisableButton.IsEnabled = false;
        try
        {
            Status.Text = await _disable();
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or UnauthorizedAccessException
                                       or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            Status.Text = Loc.T("crash.disableFailed", ex.Message);
            DisableButton.IsEnabled = true;
        }
    }

    private void Show_Click(object sender, RoutedEventArgs e)
    {
        _show();
        Application.Current.MainWindow?.Activate();
    }

    private void Catalog_Click(object sender, RoutedEventArgs e)
    {
        if (_view.ModId is not { } id) return;
        _ = _main.ShowInCatalogAsync(null, id, _view.ModName);
        Application.Current.MainWindow?.Activate();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = $"{Heading.Text} — {Profile.Text}{Environment.NewLine}{Culprit.Text}{Environment.NewLine}{Reason.Text}{Environment.NewLine}{Environment.NewLine}{Details.Text}";
        try { Clipboard.SetText(text); Status.Text = Loc.T("crash.copied"); }
        catch (System.Runtime.InteropServices.COMException) { } // буфер занят другой программой
    }

    // окно немодальное (Show, не ShowDialog): IsCancel само его не закрывает — только вызывает Click (в том числе по Esc)
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

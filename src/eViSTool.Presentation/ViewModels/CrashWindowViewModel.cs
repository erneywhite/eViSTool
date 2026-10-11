using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Diagnostics;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Что окну вылета нужно от программы: перейти к моду или серверу, каталог, главное окно, буфер обмена.</summary>
public interface ICrashWindowHost
{
    /// <summary>Сделать профиль активным и открыть вкладку (<see cref="AppTab"/>).</summary>
    void SwitchTo(GameProfile profile, int tab);
    /// <summary>Строка поиска в «Моих модах».</summary>
    void SearchMods(string text);
    /// <summary>Открыть мод в каталоге (по modid).</summary>
    Task ShowInCatalogAsync(string modId, string? name);
    void ActivateMainWindow();
    void SetClipboard(string text);
}

/// <summary>
/// Окно «Игра вылетела» / «Мир закрылся из-за ошибки» / «Сервер упал»: какой мод, похоже, виноват и почему мы так
/// думаем, и что с ним сделать — выключить, найти в программе, открыть его страницу в каталоге. Подробности (ошибка и
/// стек) — для автора мода или форума, их можно скопировать. Окно немодальное: игра (если закрылся только мир, она
/// открыта) и программа остаются доступны.
/// </summary>
public sealed partial class CrashWindowViewModel : ObservableObject
{
    private readonly CrashView _view;
    private readonly Func<Task<ModDisableResult>>? _disable;
    private readonly Action _show;
    private readonly ICrashWindowHost _host;
    // выключение уже нажато: пока идёт и после удачи кнопка недоступна, после ошибки — снова доступна
    private bool _disableUsed;

    /// <param name="disable">Выключить мод-виновник (см. <see cref="ModDisabler.For"/>); null — выключать нечего.</param>
    /// <param name="show">Перейти к моду или серверу в программе.</param>
    /// <param name="showLabel">Надпись на кнопке перехода: «Найти в „Моих модах“», «Открыть сервер».</param>
    public CrashWindowViewModel(CrashView view, Func<Task<ModDisableResult>>? disable, Action show, string showLabel, ICrashWindowHost host)
    {
        (_view, _disable, _show, _host) = (view, disable, show, host);
        Title = view.Title;
        ProfileText = Loc.T("crash.profile", view.ProfileName);
        ShowLabel = showLabel;

        if (view.ModId is not null)
        {
            CulpritText = Loc.T("crash.culprit", view.ModName ?? view.ModId, view.ModVersion ?? "");
            HowText = view.Source switch
            {
                CulpritSource.GameReport => Loc.T("crash.howGameReport"),
                CulpritSource.Stack => Loc.T("crash.howStack"),
                CulpritSource.Message => Loc.T("crash.howMessage"),
                _ => Loc.T("crash.howTranslation"),
            } + (view.ModInstalled ? "" : " " + Loc.T("crash.notInstalled"));
        }
        else
        {
            CulpritText = Loc.T("crash.unknown");
            HowText = Loc.T("crash.unknownHint");
        }
        ReasonText = Loc.T("crash.reason", view.Reason.TrimEnd('.', ' '))
                     + (view.ErrorCount > 1 ? " " + Loc.T("crash.errorCount", view.ErrorCount) : "");
        Details = string.Join(Environment.NewLine, new[] { view.Error ?? "" }.Concat(view.Stack.Select(f => "   at " + f)));

        // мода нет среди установленных — выключать нечего, даже если путь известен
        CanDisable = disable is not null && view.ModInstalled;
        HasCatalogLink = view.ModId is not null;
    }

    /// <summary>Окно вылета игры профиля: выключение мода — сразу, а пока игра открыта — после её закрытия.</summary>
    public static CrashWindowViewModel ForGame(MainViewModel main, GameProfile profile, CrashFinding finding) =>
        ForGame(profile, finding, new ModDisabler(main), new MainHost(main));

    /// <inheritdoc cref="ForGame(MainViewModel, GameProfile, CrashFinding)"/>
    public static CrashWindowViewModel ForGame(GameProfile profile, CrashFinding finding, ModDisabler disabler, ICrashWindowHost host)
    {
        var disable = finding.Culprit?.Mod is { } mod ? disabler.For(profile, mod.Path, mod.Name) : null;
        return new CrashWindowViewModel(CrashView.ForGame(profile, finding), disable, () =>
        {
            host.SwitchTo(profile, AppTab.Mods);
            if (finding.Culprit is { } c) host.SearchMods(c.ModId);
        }, Loc.T("crash.showInMods"), host);
    }

    /// <summary>Окно падения сервера: мод выключается в конфиге сервера (свой — на диске, удалённый — через агента).</summary>
    public static CrashWindowViewModel ForServer(MainViewModel main, GameProfile profile, ServerCrashInfo crash) =>
        ForServer(profile, crash, new ModDisabler(main), new MainHost(main));

    /// <inheritdoc cref="ForServer(MainViewModel, GameProfile, ServerCrashInfo)"/>
    public static CrashWindowViewModel ForServer(GameProfile profile, ServerCrashInfo crash, ModDisabler disabler, ICrashWindowHost host)
    {
        // путь — на машине сервера: у своего сервера мод читается с диска, удалённому хватит пути
        var disable = disabler.For(profile, crash.ModPath, crash.ModName ?? crash.ModId);
        return new CrashWindowViewModel(CrashView.ForServer(profile, crash), disable, () => host.SwitchTo(profile, AppTab.Server),
            Loc.T("crash.showServer"), host);
    }

    /// <summary>Что показано: заголовок, профиль, виновник, причина, ошибка и стек.</summary>
    public CrashView View => _view;

    /// <summary>«Игра вылетела», «Мир закрылся из-за ошибки», «Сервер „…“ упал» — и заголовок окна.</summary>
    public string Title { get; }
    /// <summary>«Профиль „…“».</summary>
    public string ProfileText { get; }
    /// <summary>«Похоже, виноват мод „…“ 1.2» или «Какой мод виноват — определить не удалось».</summary>
    public string CulpritText { get; }
    /// <summary>Почему так решили (или что делать, если не нашли); мода нет среди установленных — тоже здесь.</summary>
    public string HowText { get; }
    /// <summary>«Причина от игры: …» и сколько ошибок было перед этим.</summary>
    public string ReasonText { get; }
    /// <summary>Ошибка и стек («   at …» по строке) — для автора мода или форума.</summary>
    public string Details { get; }
    /// <summary>Надпись на кнопке перехода к моду или серверу.</summary>
    public string ShowLabel { get; }

    /// <summary>Кнопка «Выключить мод» видна: виновник нашёлся среди установленных и выключить его есть чем.</summary>
    public bool CanDisable { get; }
    /// <summary>Кнопка «Страница мода» видна: виновник известен по modid.</summary>
    public bool HasCatalogLink { get; }

    /// <summary>Что вышло: мод выключен (или выключится после выхода из игры), не удалось, скопировано.</summary>
    [ObservableProperty] private string _status = "";

    private bool CanRunDisable => CanDisable && !_disableUsed;

    [RelayCommand(CanExecute = nameof(CanRunDisable))]
    private async Task DisableAsync()
    {
        if (_disable is null) return;
        SetDisableUsed(true);
        var result = await _disable();
        Status = result.Message;
        if (result.Failed) SetDisableUsed(false); // можно попробовать ещё раз
    }

    private void SetDisableUsed(bool used)
    {
        _disableUsed = used;
        DisableCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Перейти к моду («Мои моды» с поиском по нему) или к серверу и показать главное окно.</summary>
    [RelayCommand]
    private void Show()
    {
        _show();
        _host.ActivateMainWindow();
    }

    /// <summary>Страница мода-виновника в каталоге программы.</summary>
    [RelayCommand]
    private void Catalog()
    {
        if (_view.ModId is not { } id) return;
        _ = _host.ShowInCatalogAsync(id, _view.ModName);
        _host.ActivateMainWindow();
    }

    /// <summary>Заголовок, профиль, виновник, причина и подробности — одним текстом, для сообщения автору мода.</summary>
    [RelayCommand]
    private void Copy()
    {
        var nl = Environment.NewLine;
        var text = $"{Title} — {ProfileText}{nl}{CulpritText}{nl}{ReasonText}{nl}{nl}{Details}";
        try
        {
            _host.SetClipboard(text);
            Status = Loc.T("crash.copied");
        }
        catch (Exception ex) when (ex is ExternalException or TimeoutException)
        {
            // буфер занят другой программой — молча, как в окне на WPF
        }
    }

    /// <summary>Программа вокруг окна: главная модель, окна программы, буфер обмена платформы.</summary>
    private sealed class MainHost(MainViewModel main) : ICrashWindowHost
    {
        public void SwitchTo(GameProfile profile, int tab) => main.SwitchTo(profile, tab);
        public void SearchMods(string text) => main.Mods.Search = text;
        public Task ShowInCatalogAsync(string modId, string? name) => main.ShowInCatalogAsync(null, modId, name);
        public void ActivateMainWindow() => AppWindows.Current.ActivateMainWindow();
        public void SetClipboard(string text) => Ui.Platform.SetClipboard(text);
    }
}

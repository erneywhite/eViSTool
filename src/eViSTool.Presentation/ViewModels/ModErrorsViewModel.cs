using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Diagnostics;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Что окну «Ошибки модов» нужно от программы: каталог, «скрыть» отчёт, буфер обмена.</summary>
public interface IModErrorsHost
{
    /// <summary>Открыть мод в каталоге (по modid).</summary>
    Task ShowInCatalogAsync(string modId, string? name);
    /// <summary>Игрок посмотрел отчёт: «!» у профиля уходит до следующего запуска с ошибками.</summary>
    void DismissErrorReport(GameProfile profile, ModErrorReport report);
    void SetClipboard(string text);
}

/// <summary>
/// Строка отчёта: мод, сколько ошибок, пример. Выключить можно, если мод нашёлся среди установленных (путь известен)
/// и у профиля есть куда его выключать (см. <see cref="ModDisabler.For"/>).
/// </summary>
public sealed partial class ModErrorRow : ObservableObject
{
    /// <param name="disable">Выключить мод (см. <see cref="ModDisabler.For"/>); null — выключать нечего.</param>
    public ModErrorRow(ModErrorLine line, Func<Task<ModDisableResult>>? disable)
    {
        Line = line;
        Disable = disable;
        CountText = Loc.T("errs.count", line.Count);
    }

    /// <summary>Мод, версия, сколько ошибок, пример.</summary>
    public ModErrorLine Line { get; }
    /// <summary>«ошибок: 37».</summary>
    public string CountText { get; }
    /// <summary>Мод можно выключить из окна: он нашёлся среди установленных, и выключать есть где.</summary>
    public bool CanDisable => Disable is not null;

    /// <summary>Выключение идёт (агент удалённого сервера отвечает не сразу): кнопка недоступна.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunDisable))]
    private bool _isBusy;

    /// <summary>Мод выключен (или выключится после выхода из игры): второй раз кнопка не нужна.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunDisable))]
    private bool _isDone;

    internal Func<Task<ModDisableResult>>? Disable { get; }

    /// <summary>Кнопку «Выключить мод» можно нажать.</summary>
    public bool CanRunDisable => CanDisable && !IsBusy && !IsDone;
}

/// <summary>
/// «Ошибки модов» за последний запуск игры или сервера профиля — по кнопке «!» у профиля. Игра их проглотила и
/// работает, но они копятся (и бывают фризы): какие моды шумят, пример ошибки, выключить, страница мода. Окно
/// немодальное; «Страница мода» и «Скрыть» его закрывают (<see cref="CloseRequested"/>).
/// </summary>
public sealed partial class ModErrorsViewModel : ObservableObject
{
    private readonly IModErrorsHost _host;

    /// <param name="disabler">Выключатель модов: путь к моду и цель профиля берутся, когда окно открывается.</param>
    public ModErrorsViewModel(GameProfile profile, ModErrorReport report, ModDisabler disabler, IModErrorsHost host)
    {
        (Profile, Report, _host) = (profile, report, host);
        Summary = Loc.T("errs.summary", profile.Name, Dates.DayMonthClock(report.At), report.Total, report.Mods.Count)
                  + (report.Unattributed > 0 ? " " + Loc.T("errs.unattributed", report.Unattributed) : "");
        Rows = report.Mods.Select(l => new ModErrorRow(l, disabler.For(profile, l.Path, l.Name))).ToList();
    }

    /// <summary>Окно главной программы: выключение через её игру и «Мои моды», каталог, отчёт профиля, буфер платформы.</summary>
    public static ModErrorsViewModel For(MainViewModel main, GameProfile profile, ModErrorReport report) =>
        new(profile, report, new ModDisabler(main), new MainHost(main));

    public GameProfile Profile { get; }
    public ModErrorReport Report { get; }

    /// <summary>«Профиль „…“, запуск 11.10 16:58: ошибок — 52, модов — 3…» и сколько ошибок без признаков мода.</summary>
    public string Summary { get; }

    /// <summary>Моды, от самого шумного (порядок отчёта).</summary>
    public IReadOnlyList<ModErrorRow> Rows { get; }

    /// <summary>Что вышло: мод выключен (или выключится после выхода из игры), не удалось, скопировано.</summary>
    [ObservableProperty] private string _status = "";

    /// <summary>Окно просит закрыться: открыли страницу мода или скрыли отчёт.</summary>
    public event Action? CloseRequested;

    private static bool CanDisable(ModErrorRow? row) => row is { CanRunDisable: true };

    /// <summary>
    /// Выключить мод строки. Строки независимы: пока выключается один мод, другой тоже можно
    /// выключать. Удалось (или отложено до выхода из игры) — кнопка строки остаётся недоступной, не удалось — снова доступна.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDisable), AllowConcurrentExecutions = true)]
    private async Task DisableAsync(ModErrorRow? row)
    {
        if (row is not { CanRunDisable: true, Disable: { } disable }) return;
        SetBusy(row, true);
        var result = await disable();
        Status = result.Message;
        row.IsDone = !result.Failed; // не удалось — можно попробовать ещё раз
        SetBusy(row, false);
    }

    private void SetBusy(ModErrorRow row, bool busy)
    {
        row.IsBusy = busy;
        DisableCommand.NotifyCanExecuteChanged(); // доступность кнопки — у каждой строки своя
    }

    /// <summary>Страница мода в каталоге программы; окно закрывается.</summary>
    [RelayCommand]
    private void Catalog(ModErrorRow? row)
    {
        if (row is null) return;
        _ = _host.ShowInCatalogAsync(row.Line.ModId, row.Line.Name);
        CloseRequested?.Invoke();
    }

    /// <summary>«Скрыть»: «!» у профиля уходит до следующего запуска с ошибками; окно закрывается.</summary>
    [RelayCommand]
    private void Dismiss()
    {
        _host.DismissErrorReport(Profile, Report);
        CloseRequested?.Invoke();
    }

    /// <summary>Сводка и по строке на мод («Имя версия — сколько: пример») — для сообщения автору мода или форума.</summary>
    [RelayCommand]
    private void Copy()
    {
        var nl = Environment.NewLine;
        var text = Summary + nl + string.Join(nl, Report.Mods.Select(m => $"{m.Name} {m.Version} — {m.Count}: {m.Example}"));
        try
        {
            _host.SetClipboard(text);
            Status = Loc.T("crash.copied");
        }
        catch (Exception ex) when (ex is ExternalException or TimeoutException)
        {
            // буфер занят другой программой — молча
        }
    }

    /// <summary>Программа вокруг окна: главная модель, буфер обмена платформы.</summary>
    private sealed class MainHost(MainViewModel main) : IModErrorsHost
    {
        public Task ShowInCatalogAsync(string modId, string? name) => main.ShowInCatalogAsync(null, modId, name);
        public void DismissErrorReport(GameProfile profile, ModErrorReport report) => main.DismissErrorReport(profile, report);
        public void SetClipboard(string text) => Ui.Platform.SetClipboard(text);
    }
}

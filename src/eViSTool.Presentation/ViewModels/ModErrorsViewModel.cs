using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Diagnostics;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Что окну «Ошибки модов» нужно от программы: каталог, игнор профиля, буфер обмена.</summary>
public interface IModErrorsHost
{
    /// <summary>Открыть мод в каталоге (по modid).</summary>
    Task ShowInCatalogAsync(string modId, string? name);
    /// <summary>Игнор профиля поменялся («Игнорировать» / «Вернуть»): сохранить, пересчитать «!» и сводку.</summary>
    void SaveErrorIgnores(GameProfile profile, ModErrorIgnores ignores);
    void SetClipboard(string text);
}

/// <summary>
/// Строка отчёта: мод (или ошибки самой игры), сколько ошибок, пример. Выключить можно, если мод нашёлся среди
/// установленных (путь известен) и у профиля есть куда его выключать (см. <see cref="ModDisabler.For"/>).
/// </summary>
public sealed partial class ModErrorRow : ObservableObject
{
    /// <param name="disable">Выключить мод (см. <see cref="ModDisabler.For"/>); null — выключать нечего.</param>
    /// <param name="kinds">Виды ошибок строки — их запоминает «Игнорировать».</param>
    public ModErrorRow(ModErrorLine line, Func<Task<ModDisableResult>>? disable, IReadOnlyList<string>? kinds = null, bool isGame = false)
    {
        Line = line;
        Disable = disable;
        Kinds = kinds ?? line.KindList;
        IsGame = isGame;
        CountText = Loc.T("errs.count", line.Count);
    }

    /// <summary>Мод, версия, сколько ошибок, пример.</summary>
    public ModErrorLine Line { get; }
    /// <summary>«ошибок: 37».</summary>
    public string CountText { get; }
    /// <summary>Ошибки без признаков мода (код самой игры): ни выключить, ни страницы мода.</summary>
    public bool IsGame { get; }
    /// <summary>Есть страница в каталоге: у мода — да, у кода самой игры — нет.</summary>
    public bool HasCatalog => !IsGame;
    /// <summary>Мод можно выключить из окна: он нашёлся среди установленных, и выключать есть где.</summary>
    public bool CanDisable => Disable is not null;

    internal IReadOnlyList<string> Kinds { get; }
    internal string IgnoreKey => IsGame ? ModErrorIgnores.Game : Line.ModId;

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
/// «Ошибки модов» за последний запуск игры или сервера профиля — по кнопке «!» у профиля или из сводки «Моих модов».
/// Игра их проглотила и работает, но они копятся (и бывают фризы): какие моды шумят, пример ошибки, выключить,
/// страница мода, «Игнорировать». В игноре — внизу, свёрнутым списком с «Вернуть»: игнор запоминает виды ошибок
/// (<see cref="ModErrorKind"/>), и новый вид у того же мода снова попадёт в счёт. Окно немодальное; «Страница мода»
/// его закрывает (<see cref="CloseRequested"/>).
/// </summary>
public sealed partial class ModErrorsViewModel : ObservableObject
{
    private readonly IModErrorsHost _host;
    private readonly ModErrorIgnores _ignores;

    /// <summary>Все строки в порядке отчёта (от самого шумного мода, ошибки игры — последними).</summary>
    private readonly List<ModErrorRow> _all;

    /// <param name="disabler">Выключатель модов: путь к моду и цель профиля берутся, когда окно открывается.</param>
    public ModErrorsViewModel(GameProfile profile, ModErrorReport report, ModErrorIgnores ignores, ModDisabler disabler, IModErrorsHost host)
    {
        (Profile, Report, _ignores, _host) = (profile, report, ignores, host);
        Summary = Loc.T("errs.summary", profile.Name, Dates.DayMonthClock(report.At), report.Total, report.Mods.Count)
                  + (report.Unattributed > 0 ? " " + Loc.T("errs.unattributed", report.Unattributed) : "");
        _all = report.Mods.Select(l => new ModErrorRow(l, disabler.For(profile, l.Path, l.Name))).ToList();
        if (report.Unattributed > 0)
            _all.Add(new ModErrorRow(
                new ModErrorLine(ModErrorIgnores.Game, Loc.T("errs.gameRow"), null, null, report.Unattributed, report.UnattributedExample ?? "", CulpritSource.Stack),
                null, report.UnattributedKindList, isGame: true));
        Place();
    }

    /// <summary>Окно главной программы: выключение через её игру и «Мои моды», каталог, игнор профиля, буфер платформы.</summary>
    public static ModErrorsViewModel For(MainViewModel main, GameProfile profile, ModErrorReport report) =>
        new(profile, report, ModErrorIgnores.Load(profile.Id), new ModDisabler(main), new MainHost(main));

    public GameProfile Profile { get; }
    public ModErrorReport Report { get; }

    /// <summary>«Профиль „…“, запуск 11.10 16:58: ошибок — 52, модов — 3…» и сколько ошибок без признаков мода.</summary>
    public string Summary { get; }

    /// <summary>Не в игноре: моды от самого шумного, ошибки игры последними.</summary>
    public ObservableCollection<ModErrorRow> Rows { get; } = [];

    /// <summary>В игноре — в том же порядке.</summary>
    public ObservableCollection<ModErrorRow> Ignored { get; } = [];

    public bool HasRows => Rows.Count > 0;
    public bool HasIgnored => Ignored.Count > 0;
    /// <summary>«В игноре (2)».</summary>
    public string IgnoredHeader => Loc.T("errs.ignoredHeader", Ignored.Count);

    /// <summary>Что вышло: мод выключен (или выключится после выхода из игры), не удалось, скопировано.</summary>
    [ObservableProperty] private string _status = "";

    /// <summary>Окно просит закрыться: открыли страницу мода.</summary>
    public event Action? CloseRequested;

    /// <summary>Разложить строки: в игноре — вниз, остальные — в список; порядок отчёта сохраняется.</summary>
    private void Place()
    {
        Rows.Clear();
        Ignored.Clear();
        foreach (var row in _all)
            (_ignores.Has(row.IgnoreKey, row.Kinds) ? Ignored : Rows).Add(row);
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasIgnored));
        OnPropertyChanged(nameof(IgnoredHeader));
    }

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
        if (row is not { HasCatalog: true }) return;
        _ = _host.ShowInCatalogAsync(row.Line.ModId, row.Line.Name);
        CloseRequested?.Invoke();
    }

    /// <summary>«Игнорировать»: виды ошибок строки запоминаются для профиля, строка уходит вниз, «!» пересчитывается.</summary>
    [RelayCommand]
    private void Ignore(ModErrorRow? row)
    {
        if (row is null || !Rows.Contains(row)) return;
        _ignores.Add(row.IgnoreKey, row.Kinds);
        _host.SaveErrorIgnores(Profile, _ignores);
        Place();
    }

    /// <summary>«Вернуть»: игнор мода (или ошибок игры) снят целиком, строка снова в списке.</summary>
    [RelayCommand]
    private void Restore(ModErrorRow? row)
    {
        if (row is null || !Ignored.Contains(row)) return;
        _ignores.Remove(row.IgnoreKey);
        _host.SaveErrorIgnores(Profile, _ignores);
        Place();
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
        public void SaveErrorIgnores(GameProfile profile, ModErrorIgnores ignores) => main.SaveErrorIgnores(profile, ignores);
        public void SetClipboard(string text) => Ui.Platform.SetClipboard(text);
    }
}

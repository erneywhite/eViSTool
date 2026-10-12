using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.AppUpdate;
using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Одна пропущенная версия в окне «Вышла новая версия»: номер, дата, итог и разделы списка изменений на языке
/// интерфейса. <see cref="IsFirst"/> — верхняя в списке: над ней нет черты-разделителя.
/// </summary>
public sealed record UpdateNotesItem(string Version, string Date, string? Summary, IReadOnlyList<ReleaseNotesSection> Sections, string PageUrl,
    bool IsFirst, bool Recommended)
{
    /// <summary>У релиза нет текста (лента GitHub без описаний): вместо списка изменений — ссылка на страницу релиза.</summary>
    public bool IsEmpty => Summary is null && Sections.Count == 0;

    /// <param name="russian">Русская часть текста релиза и дата по-русски («12 октября»); иначе английская («October 12»).</param>
    /// <param name="first">Верхняя версия в списке.</param>
    public static UpdateNotesItem From(AppRelease release, bool russian, bool first)
    {
        var notes = ReleaseNotes.Parse(release.Notes, russian);
        var culture = CultureInfo.GetCultureInfo(russian ? "ru-RU" : "en-US");
        var date = release.Published is { } at ? at.ToLocalTime().ToString(russian ? "d MMMM" : "MMMM d", culture) : "";
        return new UpdateNotesItem(release.Version.ToString(), date, notes.Summary, notes.Sections, release.PageUrl, first, release.Recommended);
    }
}

/// <summary>
/// Окно «Вышла новая версия eViSTool» (немодальное, при запуске): слева версии и кнопки, справа что изменилось за все
/// пропущенные версии на языке интерфейса. «Обновить сейчас» ставит и сразу перезапускает программу, «Позже» — до
/// следующего запуска, «Пропустить эту версию» — пока не выйдет следующая. Рекомендуемое обновление пропустить нельзя:
/// только «Позже», и окно напомнит при следующем запуске.
/// Данные окна (версии, список изменений, «рекомендуемое») берутся из <see cref="AboutViewModel"/> один раз, при
/// открытии; живая только строка состояния установки. Окно закрывается по <see cref="CloseRequested"/>, а пока идёт
/// установка, закрыть его нельзя (<see cref="CanClose"/>): файлы программы как раз подменяются.
/// </summary>
public sealed partial class UpdateWindowViewModel : ObservableObject, IDisposable
{
    private readonly AboutViewModel _about;
    private readonly Action _skip;
    private readonly Func<Task<AppRelease?>> _install;
    private readonly Action<string> _restart;
    private readonly Action<string> _openUrl;

    /// <summary>Нажали «Обновить сейчас» и установка ещё не вернулась: кнопки выключены.</summary>
    private bool _installing;

    /// <param name="about">Модель «О программе»: что нашла проверка обновлений и сама установка.</param>
    /// <param name="skip">Запомнить, что эту версию предлагать не надо.</param>
    /// <param name="install">Скачать и поставить; null — не поставилось (причина в строке состояния). По умолчанию —
    /// <see cref="AboutViewModel.InstallAsync"/> (тесты подменяют: без сети и без подмены файлов программы).</param>
    /// <param name="restart">Закрыть программу и запустить exe после выхода; по умолчанию — Ui.Platform.RestartAfterClose.</param>
    /// <param name="openUrl">Открыть ссылку в браузере; по умолчанию — Ui.OpenUrl.</param>
    public UpdateWindowViewModel(AboutViewModel about, Action skip, Func<Task<AppRelease?>>? install = null,
        Action<string>? restart = null, Action<string>? openUrl = null)
    {
        _about = about;
        _skip = skip;
        _install = install ?? about.InstallAsync;
        _restart = restart ?? (exe => Ui.Platform.RestartAfterClose(exe));
        _openUrl = openUrl ?? Ui.OpenUrl;

        OldVersion = about.AppVersion;
        NewVersion = about.AvailableUpdate?.Version.ToString() ?? "";
        IsRecommended = about.IsRecommended;
        var russian = Loc.Instance.Language == "ru";
        Releases = [.. about.MissedReleases.Select((r, i) => UpdateNotesItem.From(r, russian, i == 0))];
        _about.PropertyChanged += OnAboutChanged;
    }

    /// <summary>Какой exe запустить после обновления: главный файл программы в её папке (его подменила установка).</summary>
    public static string MainExePath => Path.Combine(AppContext.BaseDirectory, AppUpdater.WindowExe);

    /// <summary>Эта версия программы.</summary>
    public string OldVersion { get; }

    /// <summary>Новая версия (самая свежая из пропущенных); пусто — обновления нет.</summary>
    public string NewVersion { get; }

    /// <summary>Среди пропущенных версий есть рекомендуемая: плашка «Рекомендуемое обновление», номер — тёплым цветом.</summary>
    public bool IsRecommended { get; }

    /// <summary>«Пропустить эту версию» есть, только если обновление не рекомендуемое.</summary>
    public bool CanSkip => !IsRecommended;

    /// <summary>Все пропущенные версии, от новых к старым.</summary>
    public IReadOnlyList<UpdateNotesItem> Releases { get; }

    /// <summary>
    /// Строка состояния установки («Скачиваю… 40%», «Не удалось обновить: …»). Пустая при открытии, даже если в «О
    /// программе» осталась надпись от прошлой проверки: окно показывает только то, что случилось, пока оно открыто.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = "";

    public bool HasStatus => Status.Length > 0;

    /// <summary>Кнопки доступны: установка не идёт (ни из этого окна, ни из «О программе»).</summary>
    public bool CanAct => !_installing && !_about.IsUpdating;

    /// <summary>Окно можно закрыть: посреди установки нельзя — файлы программы как раз подменяются.</summary>
    public bool CanClose => !_about.IsUpdating;

    /// <summary>Окно просит закрыть себя: «Позже», «Пропустить» или обновление встало и программа перезапускается.</summary>
    public event Action? CloseRequested;

    private void OnAboutChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AboutViewModel.UpdateStatus):
                Status = _about.UpdateStatus;
                break;
            case nameof(AboutViewModel.IsUpdating):
                OnActChanged();
                OnPropertyChanged(nameof(CanClose));
                break;
        }
    }

    private void OnActChanged()
    {
        OnPropertyChanged(nameof(CanAct));
        InstallCommand.NotifyCanExecuteChanged();
        LaterCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Скачать, сверить и поставить, потом закрыть окно и перезапустить программу — один раз: сама установка
    /// (<see cref="AboutViewModel.InstallAsync"/>) программу не перезапускает. Не вышло (нет сети, файл не сошёлся) —
    /// причина уже в строке состояния, окно остаётся открытым, и можно попробовать ещё раз.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task InstallAsync()
    {
        _installing = true;
        OnActChanged();
        try
        {
            if (await _install() is null) return;
            CloseRequested?.Invoke();
            _restart(MainExePath);
        }
        finally
        {
            _installing = false;
            OnActChanged();
        }
    }

    /// <summary>«Позже»: окно закрывается, при следующем запуске программа предложит снова.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Later() => CloseRequested?.Invoke();

    /// <summary>«Пропустить эту версию»: запомнить и закрыть окно.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Skip()
    {
        _skip();
        CloseRequested?.Invoke();
    }

    /// <summary>«Что нового — на странице релиза» у версии без текста.</summary>
    [RelayCommand]
    private void OpenPage(string? url)
    {
        if (!string.IsNullOrEmpty(url)) _openUrl(url);
    }

    /// <summary>Окно закрылось: перестать следить за моделью «О программе» (она живёт, пока работает программа).</summary>
    public void Dispose() => _about.PropertyChanged -= OnAboutChanged;
}

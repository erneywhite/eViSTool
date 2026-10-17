using eViSTool.Presentation;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Game;
using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Строка списка: крупно — мод (или путь, если мод не угадан), мелко — файл.</summary>
public sealed class ModConfigRow(ModConfigEntry file)
{
    public ModConfigEntry File { get; } = file;
    public string Title => File.ModName ?? File.RelativePath; // без мода — путь с папкой: по ней часто видно, чей файл
    public string Subtitle => File.ModName is null ? Loc.T("mcfg.noMod") : File.RelativePath;
}

/// <summary>
/// Вкладка «Настройки модов»: конфиги из ModConfig активного профиля — список слева, файл справа (форма или текст).
/// Файлы — через источник (<see cref="IModConfigSource"/>): свои — с диска, сервер на другой машине — через его агента,
/// сервер у хостера — по FTP (<see cref="HostedModConfigSource"/>).
/// Сохранение проверяет JSON и откладывает прежнюю версию (там же, где файл; у хостера — на этой машине); «Вернуть
/// предыдущую версию» — шаг назад; «Сбросить к стандартным» — убрать файл, мод создаст его заново. Если файл поменяли с
/// момента открытия (другое окно, сам мод) — перед записью спрашиваем. У хостера перед каждым изменением — вопрос
/// «останови сервер в панели хостера» (память ответа — общая с «Моими модами»): запущенный сервер при остановке
/// перезапишет свои конфиги. Что операция у хостера заодно починила (вернула файл, запись которого сорвалась), окно
/// говорит после неё.
/// </summary>
public sealed partial class ModConfigViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private GameProfile? _profile;
    private IModConfigSource? _source;

    /// <summary>Сервер у хостера, чьи настройки открыты (null — свой профиль или удалённый).</summary>
    private ModTarget? _hosted;
    private string _loadedText = "";
    private DateTime? _loadedChangedUtc;
    private bool _switching;

    public ModConfigViewModel(MainViewModel main)
    {
        _main = main;
        RowsView = new FilteredList<ModConfigRow>(Rows);
        RowsView.Filter = r => (Search.Length == 0
            || r.Title.Contains(Search, StringComparison.CurrentCultureIgnoreCase)
            || r.File.RelativePath.Contains(Search, StringComparison.OrdinalIgnoreCase));
    }

    public ObservableCollection<ModConfigRow> Rows { get; } = [];
    public FilteredList<ModConfigRow> RowsView { get; }

    [ObservableProperty] private string _search = "";
    partial void OnSearchChanged(string value) => RowsView.Refresh();

    /// <summary>Почему списка нет: папки ModConfig нет, сервер не на связи и т. п. Пусто — список есть.</summary>
    [ObservableProperty] private string _notice = "";

    [ObservableProperty] private ModConfigRow? _selected;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _versions;
    [ObservableProperty] private bool _isLoading;

    // ---------- форма ----------
    // Источник правды — текст: правка поля меняет дерево JSON, из него пересобирается текст (сохранение,
    // «Вернуть предыдущую версию» и вопросы о несохранённом работают как есть). Текст поменяли иначе — форма строится заново.

    public ObservableCollection<ConfigField> Fields { get; } = [];
    private Newtonsoft.Json.Linq.JToken? _root;
    private bool _fromForm;

    /// <summary>Открыт вид «Форма» (иначе — «Текст»).</summary>
    [ObservableProperty] private bool _isFormMode;

    /// <summary>Пометка над формой: в файле комментарии, форма их не сохранит.</summary>
    [ObservableProperty] private string _formNote = "";

    /// <summary>Форма — только для JSON.</summary>
    public bool CanUseForm => Selected?.File.Kind == ModConfigKind.Json;
    public bool ShowForm => CanUseForm && IsFormMode;
    public bool ShowText => !ShowForm;
    public bool IsTextMode
    {
        get => !IsFormMode;
        set => IsFormMode = !value;
    }

    partial void OnIsFormModeChanged(bool value)
    {
        if (value && !BuildForm())
        {
            // текст сейчас не JSON — форму из него не построить
            IsFormMode = false;
            Status = Loc.T("mcfg.formNeedsJson");
            return;
        }
        OnPropertyChanged(nameof(ShowForm));
        OnPropertyChanged(nameof(ShowText));
        OnPropertyChanged(nameof(IsTextMode));
    }

    /// <summary>Построить поля из текущего текста; false — текст не разбирается как JSON.</summary>
    private bool BuildForm()
    {
        Fields.Clear();
        _root = null;
        if (!CanUseForm) return false;
        try
        {
            _root = ModConfigs.ParseJson(Text);
        }
        catch (Newtonsoft.Json.JsonReaderException)
        {
            return false;
        }
        foreach (var f in ConfigForm.Build(_root, OnFieldChanged)) Fields.Add(f);
        return true;
    }

    /// <summary>Поле изменили: при ошибке в каком-то поле текст не трогаем (и сохранять нельзя), иначе — пересобрать текст.</summary>
    private void OnFieldChanged()
    {
        OnPropertyChanged(nameof(HasFieldErrors));
        SaveCommand.NotifyCanExecuteChanged();
        if (_root is null || HasFieldErrors) return;
        _fromForm = true;
        try
        {
            Text = ModConfigs.Format(_root, _loadedText);
        }
        finally
        {
            _fromForm = false;
        }
    }

    public bool HasFieldErrors => Fields.Any(f => f.Error.Length > 0);

    /// <summary>Открыт новый файл: JSON без комментариев — сразу форма; с комментариями — текст и пометка.</summary>
    private void ChooseView()
    {
        var comments = CanUseForm && ModConfigs.HasComments(Text);
        FormNote = comments ? Loc.T("mcfg.commentsNote") : "";
        var form = CanUseForm && !comments && Text.Length > 0 && ModConfigs.JsonError(Text) is null;
        if (IsFormMode == form)
        {
            if (form) BuildForm(); // вид тот же — поля всё равно от нового файла
            else Fields.Clear();
        }
        else IsFormMode = form;
        OnPropertyChanged(nameof(CanUseForm));
        OnPropertyChanged(nameof(ShowForm));
        OnPropertyChanged(nameof(ShowText));
        OnPropertyChanged(nameof(IsTextMode));
    }

    // ---------- файл ----------

    public bool HasFile => Selected is not null;
    public string Subtitle => Loc.T("mcfg.subtitle", _profile?.Name ?? _main.ActiveProfile?.Name ?? "");
    public bool IsDirty => Selected is not null && Text != _loadedText;
    public string Heading => Selected?.Title ?? "";
    public string PathText => Selected is null ? ""
        : _source is HostedModConfigSource { Folder: { } folder } ? RemotePath.Combine(folder, Selected.File.RelativePath) // путь на сервере
        : Path.Combine(ModConfigs.FolderName, Selected.File.RelativePath);
    public string RestoreText => Versions > 0 ? Loc.T("mcfg.restoreN", Versions) : Loc.T("mcfg.restore");

    /// <summary>«Показать файл» — только для файлов на этой машине.</summary>
    public bool CanShowFile => _source?.LocalFolder is not null;

    partial void OnTextChanged(string value)
    {
        Error = Selected?.File.Kind == ModConfigKind.Json ? ModConfigs.JsonError(value) ?? "" : "";
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
        // текст поменяли не из формы (вид «Текст» не показывает форму — её перестроим при переключении)
        if (!_fromForm && IsFormMode && !BuildForm()) IsFormMode = false;
    }

    partial void OnVersionsChanged(int value)
    {
        OnPropertyChanged(nameof(RestoreText));
        RestorePreviousCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedChanging(ModConfigRow? oldValue, ModConfigRow? newValue)
    {
        // ушли с файла с правками — спросить (здесь уже без «Отмены»: список выбор уже сменил); текст — тот, что в поле сейчас
        if (_switching || oldValue is null || !IsDirty) return;
        if (Ui.Confirm(Loc.T("mcfg.askSave", oldValue.File.FileName)))
            _ = SaveAsync(oldValue, Text, _loadedChangedUtc);
    }

    partial void OnSelectedChanged(ModConfigRow? value)
    {
        Status = "";
        _ = LoadFileAsync();
        OnPropertyChanged(nameof(HasFile));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(PathText));
        ResetCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadFileAsync()
    {
        if (Selected is not { } row || _source is not { } source)
        {
            Apply(null);
            return;
        }
        try
        {
            var content = await source.ReadAsync(row.File.RelativePath);
            if (row == Selected) Apply(content); // пока читали, выбрали другой файл — его прочитает свой вызов
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (row != Selected) return;
            Apply(null);
            Status = Loc.T("mcfg.readFailed", Describe(ex));
        }
        finally
        {
            ShowRepairs(source);
        }
    }

    /// <summary>
    /// Операция у хостера заодно починила сервер (вернула файл, запись которого сорвалась; скан модов довёл установку…) —
    /// сказать об этом, каждое по разу. Источник — тот, с которым шла операция: профиль за это время могли сменить.
    /// </summary>
    private static void ShowRepairs(IModConfigSource source)
    {
        if (source is HostedModConfigSource hosted && hosted.TakeWarnings() is { Count: > 0 } warnings)
            Ui.Warn(string.Join("\n\n", warnings));
    }

    /// <summary>Показать содержимое файла (null — пусто): текст, версии, вид «форма/текст».</summary>
    private void Apply(ModConfigContent? content)
    {
        _loadedText = content?.Text ?? "";
        _loadedChangedUtc = content?.ChangedUtc;
        Text = _loadedText;
        Versions = content?.Versions ?? 0;
        if (content is { Exists: false }) Status = Loc.T("mcfg.missing");
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
        ChooseView();
    }

    // отмена у FTP — OperationCanceledException (очередь соединения), у агента — TaskCanceledException
    private static bool IsExpected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
            or HttpRequestException or OperationCanceledException;

    /// <summary>Ошибка словами: у хостера — как у модов (нет связи, неверный пароль…), у остальных — текст исключения.</summary>
    private string Describe(Exception ex) => _hosted is { } target ? ModTargets.Describe(target, ex) : ex.Message;

    private string? _focusModId;

    /// <summary>Пришли из карточки мода: в поиске — его название, после загрузки выбран его первый файл.</summary>
    public void Focus(string modId, string name)
    {
        if (!ConfirmLeave()) return;
        _focusModId = modId;
        Search = name;
    }

    /// <summary>Открыли вкладку или сменили профиль — перечитать список и открытый файл (вдруг их поменяли в другом окне).</summary>
    public async Task LoadAsync()
    {
        _profile = _main.ActiveProfile?.Model;
        OnPropertyChanged(nameof(Subtitle));
        if (_profile is null) return;
        var profile = _profile;
        if (profile.IsHosted)
        {
            // по FTP; подключение не задано или пароль не прочитать на этом компьютере — сказать, что делать
            _hosted = ModTarget.For(profile);
            _source = _hosted is not null ? new HostedModConfigSource(_hosted) : null;
            if (_source is null)
            {
                OnPropertyChanged(nameof(CanShowFile));
                Clear(ModTarget.UnreadableText(profile));
                return;
            }
        }
        else
        {
            _hosted = null;
            _source = ModTarget.For(profile) is { Remote: { } code }
                ? new RemoteModConfigSource(code)
                : new LocalModConfigSource(ModConfigService.ForProfile(profile));
        }
        OnPropertyChanged(nameof(CanShowFile));
        var source = _source;
        var keep = Selected?.File.RelativePath;
        IsLoading = true;
        try
        {
            var files = await source.ListAsync();
            if (profile != _profile) return; // пока читали, профиль сменили
            _switching = true;
            ModConfigRow? pick;
            try
            {
                Rows.Clear();
                foreach (var f in files) Rows.Add(new ModConfigRow(f));
                pick = _focusModId is { } focus
                    ? Rows.FirstOrDefault(r => string.Equals(r.File.ModId, focus, StringComparison.OrdinalIgnoreCase))
                    : Rows.FirstOrDefault(r => r.File.RelativePath == keep);
                _focusModId = null;
                Selected = null;
            }
            finally
            {
                _switching = false;
            }
            Selected = pick; // тот же файл перечитывается: его могли поменять в другом окне
            var noFolder = source.LocalFolder is { } dir ? !Directory.Exists(dir) : source is HostedModConfigSource { FolderExists: false };
            Notice = Rows.Count > 0 ? "" : noFolder ? Loc.T("mcfg.noFolder") : Loc.T("mcfg.empty");
            if (source is HostedModConfigSource) AfterHostedList(files);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (profile != _profile) return;
            Clear(profile.IsHosted ? Loc.T("mcfg.hostedFailed", Describe(ex))
                : profile.IsRemote ? Loc.T("mcfg.remoteFailed", ex.Message)
                : Loc.T("mcfg.readFailed", ex.Message));
        }
        finally
        {
            IsLoading = false;
            ShowRepairs(source);
        }
    }

    /// <summary>Список с сервера у хостера прочитан: ссылка «Настройки мода» в карточках «Моих модов» — по нему же (без запроса).</summary>
    private void AfterHostedList(IReadOnlyList<ModConfigEntry> files)
    {
        if (_main.Mods is { } mods && _main.ActiveProfile?.Model == _profile)
            mods.ShowConfigCounts(files.Where(f => f.ModId is not null).GroupBy(f => f.ModId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase));
    }

    private void Clear(string notice)
    {
        _switching = true;
        Rows.Clear();
        Selected = null;
        _switching = false;
        Apply(null);
        Notice = notice;
    }

    /// <summary>Перед сменой профиля или закрытием окна: правки — «Сохранить? Да / Нет / Отмена». false — остаться.</summary>
    public bool ConfirmLeave()
    {
        if (!IsDirty || Selected is not { } row) return true;
        switch (Ui.Ask(Loc.T("mcfg.askSave", row.File.FileName), AskButtons.YesNoCancel))
        {
            case Answer.Yes:
                // у хостера сначала «останови сервер»: «Нет» — ничего не пишем и остаёмся с правками
                return ConfirmHostedStop() && SaveBeforeLeave(row);
            case Answer.No:
                _loadedText = Text; // отказались — больше не спрашиваем
                return true;
            default:
                return false;
        }
    }

    /// <summary>Сколько перед уходом ждать записи (текст mcfg.hostedSaveTimeout называет это число; тесты укорачивают).</summary>
    internal static TimeSpan LeaveSaveWait { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Уйти можно только после записи: ждём её на месте, но не дольше <see cref="LeaveSaveWait"/> (агент недоступен или
    /// соединение с хостером занято — ошибка, а не зависшее окно). Ошибку показываем здесь же: из фоновой задачи нельзя
    /// обратиться к окну, пока оно само её ждёт, — это была бы взаимная блокировка. false — остаться с правками.
    /// </summary>
    private bool SaveBeforeLeave(ModConfigRow row)
    {
        var (text, stamp, source) = (Text, _loadedChangedUtc, _source);
        using var cts = new CancellationTokenSource(LeaveSaveWait);
        try
        {
            var content = Task.Run(() => SaveCoreAsync(row, text, stamp, interactive: false, cts.Token)).GetAwaiter().GetResult();
            return content is not null && Finish(row, text, content);
        }
        catch (OperationCanceledException) when (_hosted is not null && cts.IsCancellationRequested)
        {
            // очередь соединения профиля занята (скан модов при холодном кэше modinfo идёт минутами) или сервер молчит.
            // Отмена посреди записи файл не теряет: его прикрывают bak и отметка. У агента — как раньше, текст его ошибки
            Ui.Warn(Loc.T("mcfg.hostedSaveTimeout", row.File.FileName));
            return false;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Ui.Warn(Loc.T("mcfg.saveFailed", row.File.FileName, Describe(ex)));
            return false;
        }
        finally
        {
            if (source is not null) ShowRepairs(source);
        }
    }

    private bool CanSave() => IsDirty && Error.Length == 0 && !HasFieldErrors;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task Save() => Selected is { } row ? SaveAsync(row, Text, _loadedChangedUtc) : Task.CompletedTask;

    /// <summary>
    /// Игра или сервер профиля запущены: изменение подхватится после перезапуска, а иные моды при выходе перезаписывают
    /// конфиг. У хостера по процессам не узнать (их на этой машине нет) — там спрашивает <see cref="ConfirmHostedStop"/>.
    /// </summary>
    private bool IsRunning() =>
        _profile is { IsHosted: false } p && (p.IsRemote ? _main.ActiveProfile?.Model == p && _main.Server.IsServerUp : GameProcess.IsRunning(p));

    /// <summary>
    /// Сервер у хостера: перед записью, откатом и сбросом — «останови сервер в панели хостера» (запущенный при остановке
    /// перезапишет свои конфиги). Вопрос — про настройки модов, память ответа — общая с «Моими модами». Не хостер — true.
    /// </summary>
    private bool ConfirmHostedStop() =>
        _hosted is not { } target || _main.Mods.ConfirmIfRunning(target, Loc.T("mcfg.hostedStopAsk", target.Name));

    /// <summary>То же, но сначала быстрая проверка по логу сервера: по логу остановлен — без вопроса.</summary>
    private async Task<bool> ConfirmHostedStopAsync() =>
        _hosted is not { } target || await _main.Mods.ConfirmIfRunningAsync(target, Loc.T("mcfg.hostedStopAsk", target.Name));

    private async Task SaveAsync(ModConfigRow row, string text, DateTime? stamp)
    {
        if (!await ConfirmHostedStopAsync()) return;
        if (IsRunning()
            && !Ui.Confirm(Loc.T(_profile!.Kind == ProfileKind.Server ? "mcfg.serverRunning" : "mcfg.gameRunning")))
            return;
        var source = _source;
        try
        {
            if (await SaveCoreAsync(row, text, stamp, interactive: true) is { } content) Finish(row, text, content);
        }
        finally
        {
            if (source is not null) ShowRepairs(source);
        }
    }

    /// <summary>
    /// Записать. Файл поменяли с момента открытия — спросить, перезаписать ли (interactive = false — уходим с файла:
    /// перезаписываем, решение «сохранить» уже принято). Ошибка — предупреждение, null.
    /// </summary>
    /// <param name="interactive">
    /// Из окна (кнопка «Сохранить»): спрашивает и показывает ошибку само. Иначе — без вопросов, ошибка уходит вызывающему.
    /// </param>
    private async Task<ModConfigContent?> SaveCoreAsync(ModConfigRow row, string text, DateTime? stamp, bool interactive,
        CancellationToken ct = default)
    {
        if (_source is not { } source) return null;
        try
        {
            var result = await source.SaveAsync(new ModConfigSaveRequest(row.File.RelativePath, text, stamp), ct);
            if (result.Changed)
            {
                if (interactive && !Ui.Confirm(Loc.T("mcfg.changedOutside", row.File.FileName)))
                    return null;
                result = await source.SaveAsync(new ModConfigSaveRequest(row.File.RelativePath, text, null), ct);
            }
            return result.Content;
        }
        catch (Exception ex) when (interactive && IsExpected(ex))
        {
            Ui.Warn(Loc.T("mcfg.saveFailed", row.File.FileName, Describe(ex)));
            return null;
        }
    }

    /// <summary>Записали: если это открытый файл — он теперь «как на диске».</summary>
    private bool Finish(ModConfigRow row, string text, ModConfigContent content)
    {
        if (row == Selected)
        {
            _loadedText = text;
            _loadedChangedUtc = content.ChangedUtc;
            Versions = content.Versions;
            OnPropertyChanged(nameof(IsDirty));
            SaveCommand.NotifyCanExecuteChanged();
        }
        Status = Loc.T("mcfg.saved", row.File.FileName);
        return true;
    }

    private bool CanRestore() => Versions > 0;

    /// <summary>Шаг назад: последняя сохранённая версия встаёт на место (правки в поле — отбрасываются, с вопросом).</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestorePrevious()
    {
        if (Selected is not { } row || _source is not { } source) return;
        if (IsDirty && !Ui.Confirm(Loc.T("mcfg.askDiscard"))) return;
        if (!await ConfirmHostedStopAsync()) return;
        try
        {
            var content = await source.UndoAsync(row.File.RelativePath);
            if (row != Selected) return;
            Apply(content);
            Status = Loc.T("mcfg.restored", row.File.FileName);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Ui.Warn(Loc.T("mcfg.saveFailed", row.File.FileName, Describe(ex)));
        }
        finally
        {
            ShowRepairs(source);
        }
    }

    [RelayCommand(CanExecute = nameof(HasFile))]
    private async Task Reset()
    {
        if (Selected is not { } row || _source is not { } source) return;
        if (!Ui.Confirm(Loc.T("mcfg.askReset", row.File.FileName))) return;
        if (!await ConfirmHostedStopAsync()) return;
        try
        {
            var content = await source.ResetAsync(row.File.RelativePath);
            if (row != Selected) return;
            Apply(content);
            Status = Loc.T("mcfg.resetDone", row.File.FileName);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Ui.Warn(Loc.T("mcfg.saveFailed", row.File.FileName, Describe(ex)));
        }
        finally
        {
            ShowRepairs(source);
        }
    }

    [RelayCommand(CanExecute = nameof(HasFile))]
    private void OpenFolder()
    {
        if (_source is null) return;
        if (Selected is { } row && _source.LocalPath(row.File.RelativePath) is { } path && File.Exists(path)) Ui.ShowInFolder(path);
        else if (_source.LocalFolder is { } dir && Directory.Exists(dir)) Ui.OpenFolder(dir);
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.App.ViewModels;

/// <summary>Строка списка: крупно — мод (или имя файла, если мод не угадан), мелко — файл.</summary>
public sealed class ModConfigRow(ModConfigFile file)
{
    public ModConfigFile File { get; } = file;
    public string Title => File.ModName ?? File.RelativePath; // без мода — путь с папкой: по ней часто видно, чей файл
    public string Subtitle => File.ModName is null ? Loc.T("mcfg.noMod") : File.RelativePath;
}

/// <summary>
/// Вкладка «Настройки модов»: конфиги из ModConfig активного профиля — список слева, текст файла справа.
/// Сохранение проверяет JSON и кладёт прежнюю версию в резервные копии (<see cref="ModConfigBackups"/>);
/// «Вернуть предыдущую версию» — шаг назад; «Сбросить к стандартным» — убрать файл, мод создаст его заново.
/// Пока — только профили на этом компьютере (удалённым серверам нужны свои запросы к агенту).
/// </summary>
public sealed partial class ModConfigViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private GameProfile? _profile;
    private string? _dataDir;
    private string _loadedText = "";
    private bool _switching;

    public ModConfigViewModel(MainViewModel main)
    {
        _main = main;
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = o => o is ModConfigRow r && (Search.Length == 0
            || r.Title.Contains(Search, StringComparison.CurrentCultureIgnoreCase)
            || r.File.RelativePath.Contains(Search, StringComparison.OrdinalIgnoreCase));
    }

    public ObservableCollection<ModConfigRow> Rows { get; } = [];
    public ICollectionView RowsView { get; }

    [ObservableProperty] private string _search = "";
    partial void OnSearchChanged(string value) => RowsView.Refresh();

    /// <summary>Почему списка нет: удалённый профиль, папки ModConfig нет и т. п. Пусто — список есть.</summary>
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

    public bool HasFile => Selected is not null;
    public string Subtitle => Loc.T("mcfg.subtitle", _profile?.Name ?? _main.ActiveProfile?.Name ?? "");
    public bool IsDirty => Selected is not null && Text != _loadedText;
    public string Heading => Selected?.Title ?? "";
    public string PathText => Selected is null ? "" : Path.Combine(ModConfigs.FolderName, Selected.File.RelativePath);
    public string RestoreText => Versions > 0 ? Loc.T("mcfg.restoreN", Versions) : Loc.T("mcfg.restore");

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
        // ушли с файла с правками — спросить (здесь уже без «Отмены»: список выбор уже сменил)
        if (_switching || oldValue is null || !IsDirty) return;
        if (Dialogs.Ask(Loc.T("mcfg.askSave", oldValue.File.FileName), MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            TrySave(oldValue);
    }

    partial void OnSelectedChanged(ModConfigRow? value)
    {
        Status = "";
        LoadFile();
        OnPropertyChanged(nameof(HasFile));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(PathText));
        ResetCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    private void LoadFile()
    {
        if (Selected is not { } row)
        {
            (_loadedText, Text, Versions) = ("", "", 0);
            ChooseView();
            return;
        }
        try
        {
            _loadedText = System.IO.File.Exists(row.File.Path) ? ModConfigs.Read(row.File.Path) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _loadedText = "";
            Status = Loc.T("mcfg.readFailed", ex.Message);
        }
        Text = _loadedText;
        if (!System.IO.File.Exists(row.File.Path)) Status = Loc.T("mcfg.missing");
        Versions = Backups is { } b ? b.Versions(row.File.RelativePath).Count : 0;
        OnPropertyChanged(nameof(IsDirty));
        ChooseView();
    }

    private ModConfigBackups? Backups => _profile is null ? null : ModConfigBackups.ForProfile(_profile);

    /// <summary>Открыли вкладку или сменили профиль — перечитать список (выбор остаётся на том же файле).</summary>
    public async Task LoadAsync()
    {
        _profile = _main.ActiveProfile?.Model;
        OnPropertyChanged(nameof(Subtitle));
        if (_profile is null) return;
        if (_profile.IsRemote)
        {
            Clear(Loc.T("mcfg.remoteLater"));
            return;
        }
        _dataDir = string.IsNullOrWhiteSpace(_profile.DataDir) ? GameInstall.DefaultDataDir : _profile.DataDir;
        var keep = Selected?.File.RelativePath;
        IsLoading = true;
        try
        {
            var profile = _profile;
            var dataDir = _dataDir;
            var files = await Task.Run(() =>
            {
                var mods = ModUpdateService.ScanLocal(ProfileResolver.Resolve(profile));
                return ModConfigs.List(dataDir, mods);
            });
            if (profile != _profile) return; // пока читали, профиль сменили
            _switching = true;
            try
            {
                Rows.Clear();
                foreach (var f in files) Rows.Add(new ModConfigRow(f));
                Selected = Rows.FirstOrDefault(r => r.File.RelativePath == keep);
            }
            finally
            {
                _switching = false;
            }
            Notice = Rows.Count > 0 ? ""
                : Directory.Exists(ModConfigs.DirFor(dataDir)) ? Loc.T("mcfg.empty") : Loc.T("mcfg.noFolder");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Clear(Loc.T("mcfg.readFailed", ex.Message));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Clear(string notice)
    {
        _switching = true;
        Rows.Clear();
        Selected = null;
        _switching = false;
        Notice = notice;
    }

    /// <summary>Перед сменой профиля: правки — «Сохранить? Да / Нет / Отмена». false — остаться.</summary>
    public bool ConfirmLeave()
    {
        if (!IsDirty || Selected is not { } row) return true;
        switch (Dialogs.Ask(Loc.T("mcfg.askSave", row.File.FileName), MessageBoxButton.YesNoCancel))
        {
            case MessageBoxResult.Yes:
                return TrySave(row);
            case MessageBoxResult.No:
                _loadedText = Text; // отказались — больше не спрашиваем
                return true;
            default:
                return false;
        }
    }

    private bool CanSave() => IsDirty && Error.Length == 0 && !HasFieldErrors;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (Selected is { } row) TrySave(row);
    }

    private bool TrySave(ModConfigRow row)
    {
        if (Backups is not { } backups || _profile is null) return false;
        // игра (или свой сервер) запущена: изменения подхватятся только после перезапуска, а иные моды при выходе
        // перезаписывают свой конфиг — предупредить
        if (GameProcess.IsRunning(_profile)
            && Dialogs.Ask(Loc.T(_profile.Kind == ProfileKind.Server ? "mcfg.serverRunning" : "mcfg.gameRunning"),
                   MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return false;
        try
        {
            var text = Text; // правки — в поле редактора (и при уходе с файла: выбор ещё не сменился)
            ModConfigs.Save(row.File, text, backups);
            if (row == Selected)
            {
                _loadedText = text;
                Versions = backups.Versions(row.File.RelativePath).Count;
                OnPropertyChanged(nameof(IsDirty));
                SaveCommand.NotifyCanExecuteChanged();
            }
            Status = Loc.T("mcfg.saved", row.File.FileName);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Dialogs.Warn(Loc.T("mcfg.saveFailed", row.File.FileName, ex.Message));
            return false;
        }
    }

    private bool CanRestore() => Versions > 0;

    /// <summary>Шаг назад: последняя сохранённая версия встаёт на место (правки в поле — отбрасываются, с вопросом).</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void RestorePrevious()
    {
        if (Selected is not { } row || Backups is not { } backups) return;
        if (IsDirty && Dialogs.Ask(Loc.T("mcfg.askDiscard"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try
        {
            if (backups.Undo(row.File.Path, row.File.RelativePath))
            {
                LoadFile();
                Status = Loc.T("mcfg.restored", row.File.FileName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Warn(Loc.T("mcfg.saveFailed", row.File.FileName, ex.Message));
        }
    }

    [RelayCommand(CanExecute = nameof(HasFile))]
    private void Reset()
    {
        if (Selected is not { } row || Backups is not { } backups) return;
        if (Dialogs.Ask(Loc.T("mcfg.askReset", row.File.FileName), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try
        {
            ModConfigs.Reset(row.File, backups);
            LoadFile();
            Status = Loc.T("mcfg.resetDone", row.File.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Warn(Loc.T("mcfg.saveFailed", row.File.FileName, ex.Message));
        }
    }

    [RelayCommand(CanExecute = nameof(HasFile))]
    private void OpenFolder()
    {
        if (Selected is { } row && System.IO.File.Exists(row.File.Path)) Shell.ShowInFolder(row.File.Path);
        else if (_dataDir is not null && Directory.Exists(ModConfigs.DirFor(_dataDir))) Shell.OpenFolder(ModConfigs.DirFor(_dataDir));
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();
}

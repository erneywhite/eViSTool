using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.App.ViewModels;

/// <summary>Что с файлом конфига: ещё не читали / файла нет / не читается / загружен.</summary>
public enum ConfigLoadState { NotLoaded, Missing, Failed, Loaded }

/// <summary>Вкладки редактора.</summary>
public enum ConfigTab { General, World, Roles, Advanced }

/// <summary>
/// Редактор serverconfig.json активного серверного профиля. Файл читается лениво — при первом открытии «Конфигурации»;
/// вся работа с ним идёт через <see cref="ServerConfigDocument"/>. Пока сервер работает (или его состояние ещё
/// неизвестно) — только чтение: работающий сервер держит конфиг в памяти и сам переписывает файл.
/// </summary>
public sealed partial class ServerConfigViewModel : ObservableObject
{
    private const string SaveFilePath = "WorldConfig.SaveFileLocation";
    private const string RolesName = "Roles";

    private ServerConfigDocument? _doc;
    private string? _path;
    private string _profileName = "";

    // вкладка «Конфигурация» сейчас открыта: только тогда файл читается и перечитывается
    private bool _active;

    // null — состояние сервера ещё не выяснили (сразу после смены профиля)
    private bool? _serverRunning;

    // идёт перестройка полей: их уведомления — не правки пользователя
    private bool _building;

    private List<ConfigFieldViewModel> _fields = [];
    private IReadOnlyList<KeyValuePair<string, string>> _worldSettingsOriginal = [];
    private JToken? _rolesOriginal;
    private string? _defaultRoleOriginal;
    private IReadOnlyList<string> _roleErrors = [];
    private int _inputErrors;
    private DateTime? _savedAt;
    private string _saveError = "";

    public ServerConfigViewModel()
    {
        Roles = new RolesEditorViewModel(OnRolesChanged) { IsReadOnly = true };

        var keys = new ListCollectionView(_keyOptions);
        keys.GroupDescriptions!.Add(new PropertyGroupDescription(nameof(WorldSettingKeyOption.Category)));
        WorldSettingKeys = keys;
        WorldSettings.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasWorldSettings));
    }

    /// <summary>Вкладка «Роли» (её делает отдельная модель; здесь — только шов).</summary>
    public RolesEditorViewModel Roles { get; }

    [ObservableProperty] private ConfigLoadState _state = ConfigLoadState.NotLoaded;
    [ObservableProperty] private ConfigTab _tab = ConfigTab.General;

    /// <summary>Полный путь к serverconfig.json профиля; null — профиль не серверный.</summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>Почему файл не прочитался.</summary>
    [ObservableProperty] private string _loadError = "";

    /// <summary>Сервер работает или его состояние ещё неизвестно — править нельзя.</summary>
    [ObservableProperty] private bool _isReadOnly = true;

    /// <summary>Сервер точно работает — показываем плашку.</summary>
    [ObservableProperty] private bool _isServerRunning;

    /// <summary>Документ отличается от сохранённого.</summary>
    [ObservableProperty] private bool _isDirty;

    [ObservableProperty] private int _changeCount;
    [ObservableProperty] private int _errorCount;

    // нижняя полоса
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private RowTone _statusTone = RowTone.Muted;

    /// <summary>Перечень ошибок — в подсказке к статусу; null — ошибок нет.</summary>
    [ObservableProperty] private string? _statusTip;

    // разделы: группы полей в порядке схемы
    [ObservableProperty] private IReadOnlyList<ConfigGroupViewModel> _generalGroups = [];
    [ObservableProperty] private IReadOnlyList<ConfigGroupViewModel> _worldGroups = [];
    [ObservableProperty] private IReadOnlyList<ConfigGroupViewModel> _advancedGroups = [];

    /// <summary>Поиск на «Дополнительно»: по подписи и имени поля.</summary>
    [ObservableProperty] private string _search = "";

    [ObservableProperty] private bool _searchEmpty;

    /// <summary>Файл сохранения из WorldConfig.SaveFileLocation уже есть — мир создан.</summary>
    [ObservableProperty] private bool _worldExists;

    /// <summary>Настройки мира (WorldConfig.WorldConfiguration) строками таблицы.</summary>
    public ObservableCollection<WorldSettingRowViewModel> WorldSettings { get; } = [];

    public bool HasWorldSettings => WorldSettings.Count > 0;

    // один список известных ключей на все строки; подписи обновляются на месте — заменять его нельзя,
    // редактируемый выпадающий список при смене ItemsSource стирает свой текст
    private readonly ObservableCollection<WorldSettingKeyOption> _keyOptions = [.. KnownWorldSettings()];

    public ICollectionView WorldSettingKeys { get; }

    /// <summary>Ключи каталога: категории — в порядке каталога, внутри категории порядок тоже его (сортировка устойчивая).</summary>
    private static IEnumerable<WorldSettingKeyOption> KnownWorldSettings()
    {
        var categories = WorldSettingCatalog.All.Select(s => s.Category).Distinct().ToList();
        return WorldSettingCatalog.All.OrderBy(s => categories.IndexOf(s.Category)).Select(s => new WorldSettingKeyOption(s));
    }

    // ---------- связь с разделом «Сервер» ----------

    /// <summary>
    /// Активный профиль сменился или его правят в настройках (этот вызов приходит на каждую букву имени).
    /// Пока путь к файлу тот же — документ не трогаем. dataDir = null — профиль не серверный.
    /// </summary>
    public void OnProfileSwitched(string? dataDir, string profileName)
    {
        var path = string.IsNullOrWhiteSpace(dataDir) ? null : Path.Combine(dataDir, ProfileResolver.ServerConfigName);
        if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
        {
            _profileName = profileName;
            return;
        }

        // уходим с файла, в котором остались правки: молча их не теряем
        if (_doc is { IsDirty: true } && Ask(Loc.T("srvcfg.askSaveOnSwitch", _profileName), MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            SaveOnLeave();

        _path = path;
        _profileName = profileName;
        FilePath = path;
        _serverRunning = null;
        ApplyServerState();
        Unload();
        if (_active && path is not null) Load();
    }

    /// <summary>Открыли или закрыли вкладку «Конфигурация». Закрытие ничего не сбрасывает.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (!active) return;
        if (State == ConfigLoadState.NotLoaded)
        {
            if (_path is not null) Load();
        }
        else RefreshIfChangedOnDisk();
    }

    /// <summary>Состояние сервера выяснилось или сменилось. Остановился — файл перечитывается (сервер мог его переписать).</summary>
    public void SetServerRunning(bool running)
    {
        if (_serverRunning == running) return;
        _serverRunning = running;
        ApplyServerState();
        RefreshIfChangedOnDisk();
    }

    /// <summary>
    /// Файл изменился на диске (сервер, выключение мода на вкладке «Моды»), а своих правок нет — показываем свежее.
    /// С правками не перечитываем: о расхождении спросим при сохранении.
    /// </summary>
    public void RefreshIfChangedOnDisk()
    {
        if (!_active || _path is null) return;
        switch (State)
        {
            case ConfigLoadState.Missing when File.Exists(_path):
            case ConfigLoadState.Failed:
            case ConfigLoadState.Loaded when _doc is { IsDirty: false } doc && _inputErrors == 0 && doc.ChangedOnDisk():
                Load();
                break;
        }
    }

    /// <summary>Сменился язык: тексты, собранные в коде, — заново (модели полей не пересоздаются, правки остаются).</summary>
    public void OnLanguageChanged()
    {
        foreach (var field in _fields) field.RefreshTexts();
        foreach (var group in GeneralGroups.Concat(WorldGroups).Concat(AdvancedGroups)) group.RefreshTexts();
        foreach (var option in _keyOptions) option.RefreshTexts();
        foreach (var row in WorldSettings) row.RefreshTexts();
        ApplySearch();
        if (_doc is not null) AttachRoles(_doc); // подписи ролей и привилегий собраны в коде на прежнем языке
        Recalculate();
    }

    /// <summary>Перед запуском сервера: он прочитает файл с диска, несохранённое в него не попадёт. false — запуск отменён.</summary>
    public bool ConfirmBeforeServerStart() => ConfirmLeave(Loc.T("srvcfg.askSaveBeforeStart"));

    /// <summary>Перед закрытием окна. false — не закрывать.</summary>
    public bool ConfirmClose() => ConfirmLeave(Loc.T("srvcfg.askSaveOnClose"));

    /// <summary>«Сохранить? Да / Нет / Отмена». true — можно продолжать (сохранено либо пользователь отказался сохранять).</summary>
    private bool ConfirmLeave(string question)
    {
        if (_doc is not { IsDirty: true }) return true;
        switch (Ask(question, MessageBoxButton.YesNoCancel))
        {
            case MessageBoxResult.Yes:
                if (CanSave) return TrySave();
                Warn(Loc.T("srvcfg.cantSave"));
                return false;
            case MessageBoxResult.No:
                return true;
            default:
                return false;
        }
    }

    /// <summary>Сохранение при уходе с профиля: отменить уход уже нельзя, поэтому вопросы — только «да/нет».</summary>
    private void SaveOnLeave()
    {
        if (_doc is not { } doc) return;
        if (_roleErrors.Count > 0)
        {
            // роли с ошибками (пустой или повторяющийся код) серверу отдавать нельзя — он не запустится
            Warn(Loc.T("srvcfg.notSavedRoleErrors", string.Join(Environment.NewLine, _roleErrors)));
            return;
        }
        if (doc.ChangedOnDisk() && Ask(Loc.T("srvcfg.askOverwrite"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try
        {
            doc.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn(Loc.T("srvcfg.statusSaveFailed", ex.Message));
        }
    }

    // ---------- загрузка ----------

    private void Load()
    {
        _savedAt = null;
        _saveError = "";
        _doc = null;
        if (_path is null)
        {
            Unload();
            return;
        }

        try
        {
            if (!File.Exists(_path))
            {
                Unload(ConfigLoadState.Missing);
                return;
            }
            _doc = ServerConfigDocument.Load(_path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            Unload(ConfigLoadState.Missing);
            return;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // битый JSON, занятый файл: показываем текст ошибки и ничего не перезаписываем
            Unload(ConfigLoadState.Failed);
            LoadError = ex.Message;
            return;
        }

        LoadError = "";
        Build();
        State = ConfigLoadState.Loaded;
    }

    private void Unload(ConfigLoadState state = ConfigLoadState.NotLoaded)
    {
        _doc = null;
        _savedAt = null;
        _saveError = "";
        _building = true;
        try
        {
            _fields = [];
            GeneralGroups = WorldGroups = AdvancedGroups = [];
            WorldSettings.Clear();
            _worldSettingsOriginal = [];
            _rolesOriginal = null;
            _defaultRoleOriginal = null;
            Roles.Attach(null);
            _roleErrors = [];
            WorldExists = false;
            SearchEmpty = false;
        }
        finally
        {
            _building = false;
        }
        LoadError = "";
        State = state;
        Recalculate();
    }

    /// <summary>
    /// Построить поля по документу. Вызывается, когда документ равен файлу (загрузка, отмена правок, сохранение):
    /// значения на этот момент становятся «исходными» для пометок «изменено».
    /// </summary>
    private void Build()
    {
        var doc = _doc!;
        _building = true;
        try
        {
            _fields = [.. ServerConfigSchema.FieldsFor(doc).Select(spec => ConfigFieldViewModel.Create(doc, spec, OnFieldChanged))];
            foreach (var field in _fields) field.IsReadOnly = IsReadOnly;

            GeneralGroups = GroupsOf(f => f.Spec.Section == ConfigSections.General);
            WorldGroups = GroupsOf(f => f.Spec.Section == ConfigSections.World);
            // раздел, которого редактор не знает, — тоже в «Дополнительно»: поле не должно пропасть
            AdvancedGroups = GroupsOf(f => f.Spec.Section is not (ConfigSections.General or ConfigSections.World));
            ApplySearch();

            _worldSettingsOriginal = doc.WorldSettings;
            WorldSettings.Clear();
            foreach (var (key, value) in _worldSettingsOriginal) WorldSettings.Add(NewRow(key, value));

            _rolesOriginal = doc.Root[RolesName]?.DeepClone();
            _defaultRoleOriginal = doc.DefaultRoleCode;
            AttachRoles(doc);
            UpdateWorldExists();
        }
        finally
        {
            _building = false;
        }
        Recalculate();
    }

    private IReadOnlyList<ConfigGroupViewModel> GroupsOf(Func<ConfigFieldViewModel, bool> inSection) =>
        [.. _fields.Where(inSection)
            .GroupBy(f => f.Spec.Group)
            .Select(g => new ConfigGroupViewModel(g.Key, g.First().Spec.GroupKey, [.. g]))];

    private void AttachRoles(ServerConfigDocument doc)
    {
        var building = _building;
        _building = true;
        try
        {
            Roles.Attach(doc);
            _roleErrors = Roles.Validate();
        }
        finally
        {
            _building = building;
        }
    }

    // ---------- правки ----------

    private void OnFieldChanged(ConfigFieldViewModel field)
    {
        if (_building) return;
        if (field.Path == SaveFilePath) UpdateWorldExists();
        Touch();
    }

    private void OnRolesChanged()
    {
        if (_building || _doc is null) return;
        _roleErrors = Roles.Validate();
        Touch();
    }

    private WorldSettingRowViewModel NewRow(string key, string value) =>
        new(key, value, WorldSettingKeys, OnWorldSettingChanged, RemoveWorldSetting) { IsReadOnly = IsReadOnly };

    private void OnWorldSettingChanged(WorldSettingRowViewModel row)
    {
        if (_building) return;
        CommitWorldSettings();
    }

    private void RemoveWorldSetting(WorldSettingRowViewModel row)
    {
        if (IsReadOnly || !WorldSettings.Remove(row)) return;
        CommitWorldSettings();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddWorldSetting() => WorldSettings.Add(NewRow("", ""));

    /// <summary>Таблица настроек мира → документ. С ошибкой в таблице (повтор ключа, значение без ключа) документ не трогаем.</summary>
    private void CommitWorldSettings()
    {
        if (_doc is not { } doc) return;

        // игра сравнивает ключи без учёта регистра — повтор в другом регистре тоже повтор
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in WorldSettings)
        {
            var key = row.Key.Trim();
            row.Problem = key.Length == 0
                ? row.Value.Length > 0 ? WorldSettingProblem.NoKey : WorldSettingProblem.None
                : seen.Add(key) ? WorldSettingProblem.None : WorldSettingProblem.Duplicate;
        }

        // строка без ключа и без значения — ещё не заполнена: в файл не идёт
        if (WorldSettings.All(r => !r.HasError))
            doc.SetWorldSettings(WorldSettings.Select(r => KeyValuePair.Create(r.Key, r.Value)));
        Touch();
    }

    /// <summary>После правки: прошлые «сохранено» и ошибка записи уже неактуальны.</summary>
    private void Touch()
    {
        _savedAt = null;
        _saveError = "";
        Recalculate();
    }

    private void Recalculate()
    {
        var doc = _doc;
        var errors = new List<string>();
        var changes = 0;

        foreach (var field in _fields)
        {
            if (field.IsModified) changes++;
            if (field.HasError) errors.Add($"{field.Label}: {field.Error}");
        }

        foreach (var row in WorldSettings)
        {
            var key = row.Key.Trim();
            row.IsModified = key.Length > 0 && !_worldSettingsOriginal.Any(o => o.Key == key && o.Value == row.Value);
            if (row.IsModified) changes++;
            if (row.HasError) errors.Add($"{(key.Length > 0 ? key : Loc.T("srvcfg.wsTitle"))}: {row.Error}");
        }
        changes += _worldSettingsOriginal.Count(o => WorldSettings.All(r => r.Key.Trim() != o.Key)); // убранные

        _inputErrors = errors.Count;
        errors.AddRange(_roleErrors);

        if (doc is not null
            && (!JToken.DeepEquals(doc.Root[RolesName], _rolesOriginal) || doc.DefaultRoleCode != _defaultRoleOriginal))
            changes++;

        IsDirty = doc?.IsDirty == true;
        ChangeCount = IsDirty ? Math.Max(1, changes) : changes;
        ErrorCount = errors.Count;
        StatusTip = errors.Count == 0 ? null : string.Join(Environment.NewLine, errors.Take(12));

        (StatusText, StatusTone) =
            doc is null ? ("", RowTone.Muted)
            : _saveError.Length > 0 ? (Loc.T("srvcfg.statusSaveFailed", _saveError), RowTone.Danger)
            : ErrorCount > 0 ? (Loc.T("srvcfg.statusErrors", ErrorCount), RowTone.Danger)
            : IsDirty ? (Loc.T("srvcfg.statusDirty", ChangeCount), RowTone.Update)
            : _savedAt is { } at ? (Loc.T("srvcfg.statusSaved", at.ToString("HH:mm")), RowTone.Good)
            : (Loc.T("srvcfg.statusClean"), RowTone.Muted);

        SaveCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        AddWorldSettingCommand.NotifyCanExecuteChanged();
    }

    private void UpdateWorldExists()
    {
        var file = _doc?.Get(SaveFilePath) is JValue { Value: string text } ? text.Trim() : "";
        if (file.Length > 0 && !Path.IsPathRooted(file) && Path.GetDirectoryName(_path) is { } dataDir)
            file = Path.Combine(dataDir, file);
        WorldExists = file.Length > 0 && File.Exists(file);
    }

    partial void OnSearchChanged(string value) => ApplySearch();

    private void ApplySearch()
    {
        var search = Search.Trim();
        foreach (var group in AdvancedGroups) group.ApplySearch(search);
        SearchEmpty = AdvancedGroups.Count > 0 && AdvancedGroups.All(g => !g.IsVisible);
    }

    private void ApplyServerState()
    {
        IsServerRunning = _serverRunning == true;
        IsReadOnly = _serverRunning != false;
    }

    partial void OnIsReadOnlyChanged(bool value)
    {
        foreach (var field in _fields) field.IsReadOnly = value;
        foreach (var row in WorldSettings) row.IsReadOnly = value;
        Roles.IsReadOnly = value;
        SaveCommand.NotifyCanExecuteChanged();
        AddWorldSettingCommand.NotifyCanExecuteChanged();
    }

    // ---------- команды ----------

    private bool CanEdit => State == ConfigLoadState.Loaded && !IsReadOnly;
    private bool CanSave => CanEdit && IsDirty && ErrorCount == 0;

    // отменить можно и при работающем сервере: правки, сделанные до запуска, иначе было бы не сбросить
    private bool CanRevert => State == ConfigLoadState.Loaded && (IsDirty || _inputErrors > 0);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => TrySave();

    private bool TrySave()
    {
        if (_doc is not { } doc) return false;

        // файл уже не тот, что мы читали (сервер запускали, мод выключили на вкладке «Моды»)
        if (doc.ChangedOnDisk())
        {
            switch (Ask(Loc.T("srvcfg.askChangedOnDisk"), MessageBoxButton.YesNoCancel))
            {
                case MessageBoxResult.Yes:
                    break; // перезаписать
                case MessageBoxResult.No:
                    Load(); // перечитать, правки отбросить
                    return false;
                default:
                    return false;
            }
        }

        try
        {
            doc.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _saveError = ex.Message;
            Recalculate();
            return false;
        }

        // поля строятся заново: сохранённое стало «исходным», пометки «изменено» сброшены
        _saveError = "";
        Build();
        _savedAt = DateTime.Now;
        Recalculate();
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private void Revert()
    {
        if (_doc is not { } doc) return;
        doc.Revert();
        _savedAt = null;
        _saveError = "";
        Build();
    }

    /// <summary>Перечитать с диска.</summary>
    [RelayCommand]
    private void Reload()
    {
        if (_path is null) return;
        if (_doc is { IsDirty: true } && Ask(Loc.T("srvcfg.askReload"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        Load();
    }

    [RelayCommand]
    private void ShowFile()
    {
        if (_path is null) return;
        if (File.Exists(_path)) Shell.ShowInFolder(_path);
        else if (Path.GetDirectoryName(_path) is { } dir && Directory.Exists(dir)) Shell.OpenFolder(dir);
    }

    partial void OnStateChanged(ConfigLoadState value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        AddWorldSettingCommand.NotifyCanExecuteChanged();
    }

    private static MessageBoxResult Ask(string text, MessageBoxButton buttons) =>
        MessageBox.Show(Application.Current.MainWindow!, text, "eViSTool", buttons, MessageBoxImage.Question);

    private static void Warn(string text) =>
        MessageBox.Show(Application.Current.MainWindow!, text, "eViSTool", MessageBoxButton.OK, MessageBoxImage.Warning);
}

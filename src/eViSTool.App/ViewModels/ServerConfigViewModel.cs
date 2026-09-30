using System.Net.Http;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Config;
using eViSTool.Core.Server.Remote;
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

    // Удалённый сервер: редактор работает с копией конфига в кэше (файл _path), а читает и пишет через агента.
    // _remoteStamp — версия файла на сервере, которую мы читали: по ней агент откажет, если файл успел поменяться.
    private Func<AgentClient?>? _remote;
    private string _remoteStamp = "-";
    private DateTime? _remoteLoadedAt;  // когда менялся конфиг на сервере — на момент чтения
    private DateTime? _remoteLatestAt;  // и по последнему статусу агента
    private bool _remoteWorldExists;

    /// <summary>Конфиг удалённого сервера (файла на этой машине нет — «Показать файл» не показываем).</summary>
    [ObservableProperty] private bool _isRemote;
    private string _profileName = "";
    private string? _gameDir;

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

    // Свой сервер: пока вкладка открыта, файл могли поменять снаружи (правка с другого компьютера через агента, сервер,
    // вкладка «Моды») — раз в пару секунд сверяем отметку файла. У удалённого сервера о том же сообщает статус агента.
    private readonly System.Windows.Threading.DispatcherTimer _diskWatch = new() { Interval = TimeSpan.FromSeconds(2) };

    public ServerConfigViewModel()
    {
        _diskWatch.Tick += (_, _) => { if (!IsRemote) RefreshIfChangedOnDisk(); };
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
    public void OnProfileSwitched(string? dataDir, string profileName, string? gameDir = null,
        GameProfile? remoteProfile = null, Func<AgentClient?>? remoteClient = null)
    {
        // удалённый сервер: копия его конфига живёт в кэше этого окна
        if (remoteProfile is not null) dataDir = Path.Combine(Core.AppPaths.Cache, "remote", remoteProfile.Id);
        _remote = remoteProfile is null ? null : remoteClient;
        IsRemote = remoteProfile is not null;
        var path = string.IsNullOrWhiteSpace(dataDir) ? null : Path.Combine(dataDir, ProfileResolver.ServerConfigName);
        _gameDir = gameDir;
        GenerateCommand.NotifyCanExecuteChanged();
        if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
        {
            _profileName = profileName;
            return;
        }
        GenerateError = "";

        // уходим с файла, в котором остались правки: молча их не теряем
        if (_doc is { IsDirty: true } && Ask(Loc.T("srvcfg.askSaveOnSwitch", _profileName), MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            SaveOnLeave();

        _path = path;
        _profileName = profileName;
        FilePath = IsRemote ? Loc.T("srvcfg.remoteFile") : path;
        _remoteStamp = "-";
        _remoteLoadedAt = _remoteLatestAt = null;
        _remoteWorldExists = false;
        _serverRunning = null;
        ApplyServerState();
        Unload();
        if (_active && path is not null) Load();
    }

    /// <summary>Открыли или закрыли вкладку «Конфигурация». Закрытие ничего не сбрасывает.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (active) _diskWatch.Start();
        else _diskWatch.Stop();
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
            case ConfigLoadState.Missing when IsRemote ? _remoteLatestAt is not null : File.Exists(_path):
            case ConfigLoadState.Failed when !IsRemote || _remoteLatestAt is not null:
            case ConfigLoadState.Loaded when _doc is { IsDirty: false } doc && _inputErrors == 0 && ChangedExternally(doc):
                Load();
                break;
        }
    }

    /// <summary>Статус удалённого агента: когда менялся конфиг на сервере. Поменялся, а своих правок нет — перечитываем.</summary>
    public void ShowRemoteStatus(AgentStatus status)
    {
        if (!IsRemote || status.ConfigChangedAt == _remoteLatestAt) return;
        _remoteLatestAt = status.ConfigChangedAt;
        RefreshIfChangedOnDisk();
    }

    /// <summary>Файл поменяли не мы: на этой машине — по отметке файла, у удалённого сервера — по статусу агента.</summary>
    private bool ChangedExternally(ServerConfigDocument doc) => IsRemote ? _remoteLatestAt != _remoteLoadedAt : doc.ChangedOnDisk();

    private AgentClient RemoteClient => _remote?.Invoke() ?? throw new InvalidOperationException(Loc.T("server.stateOffline"));

    /// <summary>
    /// Запрос к удалённому агенту из синхронного кода редактора: конфиг — несколько килобайт по локальной сети,
    /// ждём не дольше 15 секунд (связи нет — ошибка, а не зависшее окно).
    /// </summary>
    private static T Sync<T>(Func<CancellationToken, Task<T>> call)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return Task.Run(() => call(cts.Token)).GetAwaiter().GetResult();
    }

    /// <summary>Конфиг, полученный от агента, — в копию в кэше; запомнить его версию.</summary>
    private void ApplyRemote(RemoteConfigFile file)
    {
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (file.Exists) File.WriteAllText(_path, file.Text);
        else if (File.Exists(_path)) File.Delete(_path);
        _remoteStamp = file.Stamp;
        _remoteLoadedAt = _remoteLatestAt = file.ChangedAt;
        _remoteWorldExists = file.WorldExists;
    }

    /// <summary>Отправить документ удалённому агенту. false — файл на сервере уже другой, ничего не записано.</summary>
    private bool UploadRemote(ServerConfigDocument doc, bool force)
    {
        var text = doc.Root.ToString(Newtonsoft.Json.Formatting.Indented);
        var result = Sync(ct => RemoteClient.SaveConfigAsync(new ConfigSaveRequest(text, _remoteStamp, force), ct));
        if (result.Conflict) return false;
        _remoteStamp = result.File.Stamp;
        _remoteLoadedAt = _remoteLatestAt = result.File.ChangedAt;
        _remoteWorldExists = result.File.WorldExists;
        return true;
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
        if (ChangedExternally(doc) && Ask(Loc.T("srvcfg.askOverwrite"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try
        {
            if (IsRemote) UploadRemote(doc, force: true); // о перезаписи уже спросили
            doc.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException
                                       or TaskCanceledException)
        {
            Warn(Loc.T("srvcfg.statusSaveFailed", IsRemote ? RemoteSecret.Describe(ex) : ex.Message));
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
            // удалённый сервер: сначала свежую копию от агента
            if (IsRemote) ApplyRemote(Sync(ct => RemoteClient.GetConfigAsync(ct)));
            if (!File.Exists(_path))
            {
                Unload(ConfigLoadState.Missing);
                return;
            }
            _doc = ServerConfigDocument.Load(_path);
        }
        catch (Exception ex) when (IsRemote && ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            Unload(ConfigLoadState.Failed);
            LoadError = RemoteSecret.Describe(ex);
            return;
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
        if (IsRemote)
        {
            WorldExists = _remoteWorldExists; // файл мира — на той машине, о нём знает агент
            return;
        }
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
        GenerateCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsReadOnlyChanged(bool value)
    {
        foreach (var field in _fields) field.IsReadOnly = value;
        foreach (var row in WorldSettings) row.IsReadOnly = value;
        Roles.IsReadOnly = value;
        SaveCommand.NotifyCanExecuteChanged();
        AddWorldSettingCommand.NotifyCanExecuteChanged();
    }

    // ---------- создание конфига для нового профиля ----------

    /// <summary>Идёт создание конфига (сервер на секунду запускается с «--setconfig»).</summary>
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private string _generateError = "";

    private bool CanGenerate => State == ConfigLoadState.Missing && !IsGenerating && _serverRunning != true
                                && _path is not null && (IsRemote || !string.IsNullOrWhiteSpace(_gameDir));

    /// <summary>Файла ещё нет: попросить сервер записать конфиг по умолчанию — чтобы настроить профиль до первого запуска.</summary>
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task Generate()
    {
        if (_path is not { } path || Path.GetDirectoryName(path) is not { } dataDir) return;
        var exe = Path.Combine(_gameDir ?? "", "VintagestoryServer.exe");
        IsGenerating = true;
        GenerateError = "";
        try
        {
            // удалённый сервер: конфиг по умолчанию запишет агент на той машине
            if (IsRemote) ApplyRemote(await RemoteClient.GenerateConfigAsync());
            else await ServerConfigGenerator.GenerateAsync(exe, dataDir);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception or HttpRequestException or TaskCanceledException)
        {
            GenerateError = Loc.T("srvcfg.generateFailed", IsRemote ? RemoteSecret.Describe(ex) : ex.Message);
        }
        finally
        {
            IsGenerating = false;
        }
        // пока создавали, профиль могли сменить — тогда этот файл уже не наш
        if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase) && _active) Load();
        GenerateCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsGeneratingChanged(bool value) => GenerateCommand.NotifyCanExecuteChanged();

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

        // файл уже не тот, что мы читали (сервер запускали, мод выключили на вкладке «Моды», правили с другого компьютера)
        var force = false;
        if (ChangedExternally(doc))
        {
            if (!AskOverwrite()) return false;
            force = true;
        }

        try
        {
            // удалённый сервер: сначала агенту (он проверит, что файл тот же, что мы читали), потом — копия в кэше
            if (IsRemote && !UploadRemote(doc, force))
            {
                if (!AskOverwrite()) return false;
                UploadRemote(doc, force: true);
            }
            doc.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException
                                       or TaskCanceledException)
        {
            _saveError = IsRemote ? RemoteSecret.Describe(ex) : ex.Message;
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

    /// <summary>«Файл уже другой: перезаписать?» Да — перезаписать; Нет — перечитать и отбросить правки; Отмена — ничего.</summary>
    private bool AskOverwrite()
    {
        switch (Ask(Loc.T("srvcfg.askChangedOnDisk"), MessageBoxButton.YesNoCancel))
        {
            case MessageBoxResult.Yes:
                return true;
            case MessageBoxResult.No:
                Load();
                return false;
            default:
                return false;
        }
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
        GenerateCommand.NotifyCanExecuteChanged();
    }

    private static MessageBoxResult Ask(string text, MessageBoxButton buttons) =>
        MessageBox.Show(Application.Current.MainWindow!, text, "eViSTool", buttons, MessageBoxImage.Question);

    private static void Warn(string text) =>
        MessageBox.Show(Application.Current.MainWindow!, text, "eViSTool", MessageBoxButton.OK, MessageBoxImage.Warning);
}

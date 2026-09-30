using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Server.Config;
using Newtonsoft.Json.Linq;

namespace eViSTool.App.ViewModels;

/// <summary>
/// Вкладка «Роли» редактора serverconfig: список ролей, роль для новых игроков и редактор выбранной роли.
/// Верная правка сразу уходит в документ; «несохранённость» считает родитель по документу — ему сообщается через changed.
/// </summary>
public sealed partial class RolesEditorViewModel : ObservableObject
{
    private readonly Action _changed;

    // роли на момент загрузки/сохранения/отмены — от них считаются пометки «изменено»; ключ — сам объект роли в документе
    private readonly Dictionary<JObject, JObject> _baseline = new(ReferenceEqualityComparer.Instance);
    private string? _baselineDefault;

    private ServerConfigDocument? _document;
    private RoleItemViewModel? _selectedRole;
    private RoleItemViewModel? _defaultRole;
    private bool _building;        // список перестраивается: ListBox и ComboBox в это время сами сбрасывают выбор
    private bool _recheckingCodes;

    /// <param name="changed">Вызывать после каждой правки, записанной в документ (родитель пересчитает «несохранённость» и ошибки).</param>
    public RolesEditorViewModel(Action changed)
    {
        _changed = changed;
        // подписи привилегий, режимов и ошибок собраны в коде — при смене языка их нужно собрать заново
        Loc.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Loc.Language)) Attach(_document);
        };
    }

    /// <summary>Сервер работает — всё только для чтения.</summary>
    [ObservableProperty] private bool _isReadOnly;

    [ObservableProperty] private ObservableCollection<RoleItemViewModel> _roles = [];

    /// <summary>Документ есть (файл конфигурации загружен).</summary>
    [ObservableProperty] private bool _hasDocument;

    /// <summary>Ошибка над списком: ролей нет или роль для новых игроков не найдена — с таким конфигом сервер не запустится.</summary>
    [ObservableProperty] private string? _defaultRoleError;

    /// <summary>Роль для новых игроков не та, что была при загрузке.</summary>
    [ObservableProperty] private bool _isDefaultRoleChanged;

    public bool CanEdit => !IsReadOnly;
    public bool HasSelection => _selectedRole is not null;

    public RoleItemViewModel? SelectedRole
    {
        get => _selectedRole;
        set
        {
            if (_building || ReferenceEquals(value, _selectedRole)) return;
            _selectedRole = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            NotifyCommands();
        }
    }

    /// <summary>Роль, которую получает новый игрок (DefaultRoleCode). null — код в файле не совпал ни с одной ролью.</summary>
    public RoleItemViewModel? DefaultRole
    {
        get => _defaultRole;
        set
        {
            // null присылает сам ComboBox, когда меняется список, — «никакую» роль выбрать нельзя
            if (_building || value is null || ReferenceEquals(value, _defaultRole) || _document is null || IsReadOnly) return;
            _document.DefaultRoleCode = value.Code;
            RefreshDefault();
            _changed();
        }
    }

    /// <summary>Что написать справа, пока роль не выбрана.</summary>
    public string PickText => Loc.T(Roles.Count == 0 ? "roles.empty" : "roles.pick");

    public string DuplicateTip =>Loc.T(IsReadOnly ? "roles.readOnlyTip" : "roles.duplicateTip");

    /// <summary>Подсказка кнопки «Удалить»: если удалить нельзя — почему.</summary>
    public string DeleteTip => Loc.T(
        IsReadOnly ? "roles.readOnlyTip"
        : _selectedRole is null ? "roles.deleteTip"
        : _selectedRole.Entry.IsStandard ? "roles.deleteTipStandard"
        : IsDefaultCode(_selectedRole) ? "roles.deleteTipDefault"
        : "roles.deleteTip");

    // ---- то, чем пользуются роли

    /// <summary>Привилегии из файла по всем ролям (сейчас и на момент загрузки) — среди них могут быть привилегии от модов.</summary>
    internal IReadOnlyList<string> FilePrivileges { get; private set; } = [];

    /// <summary>Режимы игры с подписями на текущем языке.</summary>
    internal IReadOnlyList<Choice<int>> GameModes { get; private set; } = [];

    /// <summary>Коды остальных ролей — как они записаны в документе.</summary>
    internal IEnumerable<string> CodesExcept(RoleItemViewModel role) =>
        Roles.Where(other => !ReferenceEquals(other, role)).Select(other => other.Entry.Code);

    internal void OnRoleEdited() => _changed();

    internal void OnRoleCodeChanged(RoleItemViewModel role)
    {
        if (_document is null) return;
        // переименовали роль для новых игроков — ссылка едет следом, иначе сервер не запустится
        if (ReferenceEquals(role, _defaultRole)) _document.DefaultRoleCode = role.Entry.Code;
        RecheckCodes(role);
        RefreshDefault();
    }

    // ---- шов с редактором конфига

    /// <summary>
    /// Новый документ (или null — файла нет): перестроить список ролей. Вызывается при загрузке, отмене правок, сохранении
    /// и смене языка. Выбранная роль остаётся выбранной, если роль с таким кодом ещё есть.
    /// </summary>
    public void Attach(ServerConfigDocument? document)
    {
        var keepCode = _selectedRole?.Entry.Code;
        // Тот же документ и в нём есть несохранённое — значит, зовут не из-за загрузки, сохранения или отмены
        // (после них документ «чистый»), а чтобы пересобрать тексты. Точку отсчёта пометок «изменено» не трогаем.
        var keepBaseline = document is not null && ReferenceEquals(document, _document) && document.IsDirty;
        _document = document;

        var entries = document?.Roles ?? [];
        if (!keepBaseline)
        {
            _baseline.Clear();
            foreach (var entry in entries) _baseline[entry.Json] = (JObject)entry.Json.DeepClone();
            _baselineDefault = document?.DefaultRoleCode;
        }

        FilePrivileges = [.. entries.SelectMany(e => e.Privileges), .. _baseline.Values.SelectMany(json => new RoleEntry(json).Privileges)];
        GameModes = [.. RoleCatalog.GameModes.Select(mode => new Choice<int>(Loc.T(RoleCatalog.GameModeLabelKey(mode)), mode))];

        _building = true;
        try
        {
            Roles = new ObservableCollection<RoleItemViewModel>(
                entries.Select(entry => new RoleItemViewModel(this, entry, _baseline.GetValueOrDefault(entry.Json))));
            _selectedRole = Roles.FirstOrDefault(r => r.Entry.Code == keepCode)
                            ?? Roles.FirstOrDefault(r => r.Entry.Code == document?.DefaultRoleCode)
                            ?? Roles.FirstOrDefault();
        }
        finally
        {
            _building = false;
        }

        HasDocument = document is not null;
        RecheckCodes(null);
        RefreshDefault();
        OnPropertyChanged(nameof(SelectedRole));
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>Тексты ошибок (уже локализованные). Пусто — можно сохранять.</summary>
    public IReadOnlyList<string> Validate()
    {
        if (_document is null) return [];
        var errors = new List<string>();
        if (DefaultRoleError is { } top) errors.Add(top);
        foreach (var role in Roles) errors.AddRange(role.Errors());
        return errors;
    }

    partial void OnIsReadOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        foreach (var role in Roles) role.NotifyReadOnlyChanged();
        NotifyCommands();
    }

    // ---- дублирование и удаление

    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private void Duplicate()
    {
        if (_document is null || _selectedRole is not { } source) return;
        var dialog = new DuplicateRoleWindow(source.Entry.Code, source.DisplayName, [.. Roles.Select(r => r.Entry.Code)])
        {
            Owner = Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            DuplicateAs(source, dialog.RoleCode, dialog.RoleName);
        }
        catch (InvalidOperationException ex)
        {
            // окно код уже проверило; сюда попадём, только если правила ядра строже
            MessageBox.Show(Application.Current.MainWindow, ex.Message, "eViSTool", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Копия роли в конец списка; новая роль становится выбранной. Код занят или пуст — InvalidOperationException.</summary>
    internal void DuplicateAs(RoleItemViewModel source, string code, string name)
    {
        if (_document is null) return;
        var role = new RoleItemViewModel(this, _document.DuplicateRole(source.Entry, code, name), baseline: null);
        Roles.Add(role);
        role.CheckCode();
        SelectedRole = role;
        _changed();
    }

    private bool CanDuplicate() => !IsReadOnly && _document is not null && _selectedRole is not null;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (_selectedRole is not { } role) return;
        var question = Loc.T("roles.deleteConfirm", role.DisplayName, role.Entry.Code);
        if (MessageBox.Show(Application.Current.MainWindow, question, "eViSTool", MessageBoxButton.YesNo, MessageBoxImage.Question)
            == MessageBoxResult.Yes)
            Remove(role);
    }

    /// <summary>Убрать роль из документа и списка; выбранной становится соседняя.</summary>
    internal void Remove(RoleItemViewModel role)
    {
        var index = Roles.IndexOf(role);
        if (_document is null || index < 0 || !_document.RemoveRole(role.Entry)) return;
        Roles.Remove(role);
        SelectedRole = Roles.Count == 0 ? null : Roles[Math.Min(index, Roles.Count - 1)];
        // код удалённой роли освободился — у соседей могла пропасть ошибка «занят»
        RecheckCodes(null);
        RefreshDefault();
        _changed();
    }

    private bool CanDelete() =>
        !IsReadOnly && _document is not null && _selectedRole is { } role && !role.Entry.IsStandard && !IsDefaultCode(role);

    // роль для новых игроков удалить нельзя; регистр не учитываем — так же решает и документ (RemoveRole)
    private bool IsDefaultCode(RoleItemViewModel role) =>
        string.Equals(role.Entry.Code, _document?.DefaultRoleCode, StringComparison.OrdinalIgnoreCase);

    // ---- пересчёты

    private void RecheckCodes(RoleItemViewModel? except)
    {
        // проверка может записать код соседней роли (он был занят и освободился) — а та снова попросит проверить всех
        if (_recheckingCodes) return;
        _recheckingCodes = true;
        try
        {
            foreach (var role in Roles)
                if (!ReferenceEquals(role, except))
                    role.CheckCode();
        }
        finally
        {
            _recheckingCodes = false;
        }
    }

    private void RefreshDefault()
    {
        var code = _document?.DefaultRoleCode;
        // регистр важен: сервер ищет роль по точному коду
        _defaultRole = Roles.FirstOrDefault(r => r.Entry.Code == code);
        foreach (var role in Roles) role.IsDefault = ReferenceEquals(role, _defaultRole);

        DefaultRoleError =
            _document is null ? null
            : Roles.Count == 0 ? Loc.T("roles.errNoRoles")
            : _defaultRole is not null ? null
            : string.IsNullOrEmpty(code) ? Loc.T("roles.errDefaultRoleNone")
            : Loc.T("roles.errDefaultRole", code);
        IsDefaultRoleChanged = _document is not null && code != _baselineDefault;
        OnPropertyChanged(nameof(DefaultRole));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        DuplicateCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(DuplicateTip));
        OnPropertyChanged(nameof(DeleteTip));
        OnPropertyChanged(nameof(PickText));
    }
}

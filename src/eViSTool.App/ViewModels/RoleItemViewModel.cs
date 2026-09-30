using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Server.Config;
using Newtonsoft.Json.Linq;

namespace eViSTool.App.ViewModels;

/// <summary>
/// Одна роль: строка в списке слева и поля редактора справа. В полях лежит то, что набрал пользователь; в документ
/// (через <see cref="Entry"/>) уходит только верный ввод — при ошибке там остаётся прежнее значение, а поле показывает ошибку.
/// </summary>
public sealed partial class RoleItemViewModel : ObservableObject
{
    private const string NameField = "Name", DescriptionField = "Description", ColorField = "Color";

    private readonly RolesEditorViewModel _owner;
    private readonly JObject? _baseline; // роль на момент загрузки/сохранения; null — роль создана только что
    private IReadOnlyList<PrivilegeItemViewModel>? _privileges;
    private Choice<int> _selectedGameMode;
    private string? _minXError, _minYError, _minZError;
    private bool _bulk;

    [ObservableProperty] private string _codeText;
    [ObservableProperty] private string? _codeError;
    [ObservableProperty] private string _nameText;
    [ObservableProperty] private string _descriptionText;
    [ObservableProperty] private string _privilegeLevelText;
    [ObservableProperty] private string? _privilegeLevelError;
    [ObservableProperty] private bool _autoGrant;
    [ObservableProperty] private string _colorText;
    [ObservableProperty] private string? _colorError;
    [ObservableProperty] private string _claimAllowanceText;
    [ObservableProperty] private string? _claimAllowanceError;
    [ObservableProperty] private string _claimMaxAreasText;
    [ObservableProperty] private string? _claimMaxAreasError;
    [ObservableProperty] private string _claimMinXText;
    [ObservableProperty] private string _claimMinYText;
    [ObservableProperty] private string _claimMinZText;

    /// <summary>JSON роли отличается от того, что было при загрузке (сохранении).</summary>
    [ObservableProperty] private bool _isChanged;

    /// <summary>Роль для новых игроков (DefaultRoleCode).</summary>
    [ObservableProperty] private bool _isDefault;

    public RoleItemViewModel(RolesEditorViewModel owner, RoleEntry entry, JObject? baseline)
    {
        _owner = owner;
        Entry = entry;
        _baseline = baseline;
        // запоминаем один раз: иначе поле кода заблокировалось бы посреди ввода, стоило набрать код стандартной роли
        IsStandard = entry.IsStandard;

        _codeText = entry.Code;
        _nameText = entry.Name;
        _descriptionText = entry.Description;
        _privilegeLevelText = Number(entry.PrivilegeLevel);
        _autoGrant = entry.AutoGrant;
        _colorText = entry.Color;
        _claimAllowanceText = Number(entry.LandClaimAllowance);
        _claimMaxAreasText = Number(entry.LandClaimMaxAreas);
        _claimMinXText = Number(entry.LandClaimMinX);
        _claimMinYText = Number(entry.LandClaimMinY);
        _claimMinZText = Number(entry.LandClaimMinZ);

        // режим не из списка (вписали руками) — показываем числом, чтобы не подменять его молча
        var mode = entry.DefaultGameMode;
        GameModes = owner.GameModes.Any(m => m.Value == mode) ? owner.GameModes : [.. owner.GameModes, new Choice<int>(Number(mode), mode)];
        _selectedGameMode = GameModes.First(m => m.Value == mode);

        // то, что уже лежит в файле, тоже проверяем: с плохим цветом сервер не запустится
        _colorError = CheckColor(_colorText);
        _claimAllowanceError = CheckInt(_claimAllowanceText, 0, out _);
        _claimMaxAreasError = CheckInt(_claimMaxAreasText, 0, out _);
        _minXError = CheckInt(_claimMinXText, 0, out _);
        _minYError = CheckInt(_claimMinYText, 0, out _);
        _minZError = CheckInt(_claimMinZText, 0, out _);
        _isChanged = DiffersFromBaseline();
    }

    public RoleEntry Entry { get; }

    /// <summary>Роль была стандартной, когда её загрузили: код такой роли не правится.</summary>
    public bool IsStandard { get; }

    // ---- строка в списке

    public string Code => Entry.Code;
    public string DisplayName => Entry.Name.Length > 0 ? Entry.Name : Entry.Code;
    public string LevelText => Loc.T("roles.levelShort", Entry.PrivilegeLevel);

    /// <summary>Цвет для образца; null — в поле не цвет.</summary>
    public RoleRgb? Swatch => RoleCatalog.TryParseColor(ColorText, out var rgb) ? rgb : null;

    // экранные дикторы и автоматизация читают элемент выпадающего списка через ToString
    public override string ToString() => Entry.Name.Length > 0 ? $"{Entry.Name} ({Entry.Code})" : Entry.Code;

    // ---- только чтение

    public bool CanEdit => _owner.CanEdit;
    public bool IsLocked => !_owner.CanEdit;
    public bool IsCodeLocked => IsStandard || !_owner.CanEdit;
    public string CodeHint => Loc.T(IsStandard ? "roles.codeHintStandard" : "roles.codeHint");

    internal void NotifyReadOnlyChanged()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(IsCodeLocked));
        GrantAllCommand.NotifyCanExecuteChanged();
        RevokeAllCommand.NotifyCanExecuteChanged();
    }

    // ---- карточка «Роль»

    partial void OnCodeTextChanged(string value) => CheckCode();

    /// <summary>
    /// Проверить код и записать его, если он годится. Зовётся и для соседних ролей: «занят» зависит от чужих кодов,
    /// и код, который был занят, может освободиться.
    /// </summary>
    internal void CheckCode()
    {
        var problem = RoleCatalog.CheckCode(CodeText, _owner.CodesExcept(this));
        CodeError = CodeProblemText(problem, CodeText);
        if (problem != RoleCodeProblem.None || IsStandard || !_owner.CanEdit || Entry.Code == CodeText) return;

        Entry.Code = CodeText;
        OnPropertyChanged(nameof(Code));
        OnPropertyChanged(nameof(DisplayName));
        _owner.OnRoleCodeChanged(this);
        Touched();
    }

    internal static string? CodeProblemText(RoleCodeProblem problem, string code) => problem switch
    {
        RoleCodeProblem.Empty => Loc.T("roles.errCodeEmpty"),
        RoleCodeProblem.Whitespace => Loc.T("roles.errCodeSpaces"),
        RoleCodeProblem.Taken => Loc.T("roles.errCodeTaken", code),
        _ => null,
    };

    partial void OnNameTextChanged(string value)
    {
        if (!_owner.CanEdit) return;
        WriteText(NameField, value);
        OnPropertyChanged(nameof(DisplayName));
        Touched();
    }

    partial void OnDescriptionTextChanged(string value)
    {
        if (!_owner.CanEdit) return;
        WriteText(DescriptionField, value);
        Touched();
    }

    partial void OnPrivilegeLevelTextChanged(string value)
    {
        PrivilegeLevelError = CheckInt(value, int.MinValue, out var level);
        if (PrivilegeLevelError is not null || !_owner.CanEdit) return;
        Entry.PrivilegeLevel = level;
        OnPropertyChanged(nameof(LevelText));
        Touched();
    }

    partial void OnAutoGrantChanged(bool value)
    {
        if (!_owner.CanEdit) return;
        Entry.AutoGrant = value;
        Touched();
    }

    // ---- карточка «Оформление и режим»

    /// <summary>Названия цветов для выпадающего списка.</summary>
    public IReadOnlyList<RoleColorName> ColorChoices => RoleCatalog.Colors;

    partial void OnColorTextChanged(string value)
    {
        ColorError = CheckColor(value);
        OnPropertyChanged(nameof(Swatch));
        var text = value.Trim();
        // пустое без ошибки — это «поля Color в файле нет»: писать нечего
        if (ColorError is not null || text.Length == 0 || !_owner.CanEdit) return;
        // название пишем так, как его пишет сам сервер
        Entry.Color = RoleCatalog.FindColor(text)?.Name ?? text;
        Touched();
    }

    private string? CheckColor(string? text)
    {
        // поля нет вовсе — сервер возьмёт цвет по умолчанию; а вот null или число в поле он не прочитает
        if (string.IsNullOrWhiteSpace(text) && Entry.Json.Property(ColorField) is null) return null;
        return RoleCatalog.TryParseColor(text, out _) ? null : Loc.T("roles.errColor");
    }

    /// <summary>Режимы игры; у роли с незнакомым числом в файле — ещё и оно.</summary>
    public IReadOnlyList<Choice<int>> GameModes { get; }

    public Choice<int> SelectedGameMode
    {
        get => _selectedGameMode;
        set
        {
            // null присылает сам ComboBox, когда меняется список вариантов (выбрали другую роль), — это не выбор пользователя
            if (value is null || value == _selectedGameMode) return;
            _selectedGameMode = value;
            OnPropertyChanged();
            if (!_owner.CanEdit) return;
            Entry.DefaultGameMode = value.Value;
            Touched();
        }
    }

    // ---- карточка «Приват»

    partial void OnClaimAllowanceTextChanged(string value)
    {
        ClaimAllowanceError = CheckInt(value, 0, out var blocks);
        if (ClaimAllowanceError is not null || !_owner.CanEdit) return;
        Entry.LandClaimAllowance = blocks;
        Touched();
    }

    partial void OnClaimMaxAreasTextChanged(string value)
    {
        ClaimMaxAreasError = CheckInt(value, 0, out var areas);
        if (ClaimMaxAreasError is not null || !_owner.CanEdit) return;
        Entry.LandClaimMaxAreas = areas;
        Touched();
    }

    partial void OnClaimMinXTextChanged(string value) => ApplyMinSize(value, ref _minXError, size => Entry.LandClaimMinX = size);
    partial void OnClaimMinYTextChanged(string value) => ApplyMinSize(value, ref _minYError, size => Entry.LandClaimMinY = size);
    partial void OnClaimMinZTextChanged(string value) => ApplyMinSize(value, ref _minZError, size => Entry.LandClaimMinZ = size);

    /// <summary>Одна строка ошибки на три поля размера.</summary>
    public string? ClaimMinError => _minXError ?? _minYError ?? _minZError;

    private void ApplyMinSize(string text, ref string? error, Action<int> write)
    {
        error = CheckInt(text, 0, out var size);
        OnPropertyChanged(nameof(ClaimMinError));
        if (error is not null || !_owner.CanEdit) return;
        write(size);
        Touched();
    }

    // ---- карточка «Привилегии»

    /// <summary>Строятся при первом показе роли: в списке слева они не нужны.</summary>
    public IReadOnlyList<PrivilegeItemViewModel> Privileges => _privileges ??= BuildPrivileges();

    public string PrivilegeCountText => Loc.T("roles.privCount", Privileges.Count(p => p.IsGranted), Privileges.Count);

    /// <summary>Привилегии, выданные модами на работающем сервере: сервер ведёт список сам, здесь он только виден.</summary>
    public string RuntimePrivilegesText =>
        Entry.RuntimePrivileges is { Count: > 0 } list ? Loc.T("roles.runtimePrivileges", string.Join(", ", list)) : "";

    private IReadOnlyList<PrivilegeItemViewModel> BuildPrivileges()
    {
        var granted = Entry.Privileges.ToHashSet(StringComparer.Ordinal);
        // привилегии от модов собраны по всем ролям — так мод-привилегию админа можно выдать и другой роли
        var codes = RoleCatalog.PrivilegesToShow(_owner.FilePrivileges.Concat(Entry.Privileges));
        return [.. codes.Select(code => new PrivilegeItemViewModel(this, code, granted.Contains(code)))];
    }

    /// <summary>Выдать всё, что знает игра. Запреты и привилегии от модов остаются как были: что они делают, программа не знает.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void GrantAll() => SetAll(p => p.IsGrantable || p.IsGranted);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RevokeAll() => SetAll(_ => false);

    private void SetAll(Func<PrivilegeItemViewModel, bool> state)
    {
        // одна правка — одно оповещение, а не по штуке на привилегию
        _bulk = true;
        try
        {
            foreach (var privilege in Privileges) privilege.IsGranted = state(privilege);
        }
        finally
        {
            _bulk = false;
        }
        OnPropertyChanged(nameof(PrivilegeCountText));
        Touched();
    }

    internal void OnPrivilegeToggled(PrivilegeItemViewModel privilege)
    {
        Entry.SetPrivilege(privilege.Code, privilege.IsGranted);
        if (_bulk) return;
        OnPropertyChanged(nameof(PrivilegeCountText));
        Touched();
    }

    // ---- проверки

    /// <summary>Ошибки роли для общего списка перед сохранением: «Роль «vip» — Цвет: …».</summary>
    internal IEnumerable<string> Errors()
    {
        (string LabelKey, string? Error)[] fields =
        [
            ("roles.code", CodeError),
            ("roles.level", PrivilegeLevelError),
            ("roles.color", ColorError),
            ("roles.claimAllowance", ClaimAllowanceError),
            ("roles.claimMaxAreas", ClaimMaxAreasError),
            ("roles.claimMinSize", ClaimMinError),
        ];
        var title = DisplayName.Length > 0 ? DisplayName : "?";
        return fields.Where(f => f.Error is not null).Select(f => Loc.T("roles.errInRole", title, Loc.T(f.LabelKey), f.Error));
    }

    private static string? CheckInt(string? text, int min, out int value)
    {
        if (!int.TryParse((text ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
            return Loc.T(min >= 0 ? "roles.errNotNegative" : "roles.errInteger");
        return value < min ? Loc.T("roles.errNotNegative") : null;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    // ---- запись

    /// <summary>
    /// Текстовое поле роли. Пустой ввод там, где при загрузке строки не было (null или поля нет), возвращает всё как было:
    /// стёр набранное — документ снова «чистый», а не с "" на месте null.
    /// </summary>
    private void WriteText(string field, string value)
    {
        if (value.Length == 0 && _baseline is not null && _baseline[field] is not JValue { Type: JTokenType.String })
        {
            if (_baseline.Property(field) is { } was) Entry.Json[field] = was.Value.DeepClone();
            else Entry.Json.Remove(field);
            return;
        }

        if (field == NameField) Entry.Name = value;
        else Entry.Description = value;
    }

    private bool DiffersFromBaseline() => _baseline is null || !JToken.DeepEquals(Entry.Json, _baseline);

    /// <summary>В документ ушла правка: обновить пометку «изменено» и сказать редактору.</summary>
    private void Touched()
    {
        IsChanged = DiffersFromBaseline();
        _owner.OnRoleEdited();
    }
}

/// <summary>Переключатель одной привилегии роли.</summary>
public sealed class PrivilegeItemViewModel : ObservableObject
{
    private readonly RoleItemViewModel _role;
    private bool _isGranted;

    internal PrivilegeItemViewModel(RoleItemViewModel role, string code, bool granted)
    {
        _role = role;
        Code = code;
        _isGranted = granted;
        IsKnown = RoleCatalog.IsKnownPrivilege(code);
        // привилегию от мода подписать нечем — показываем её код
        Label = IsKnown ? Loc.T(RoleCatalog.PrivilegeLabelKey(code)) : code;
        Hint = IsKnown ? Loc.T(RoleCatalog.PrivilegeHintKey(code)) : Loc.T("roles.privUnknown");
    }

    public string Code { get; }
    public string Label { get; }
    public string Hint { get; }

    /// <summary>Привилегия из списков программы; иначе — от мода или неизвестная.</summary>
    public bool IsKnown { get; }

    /// <summary>Код отдельной строкой — только там, где подпись не он сам.</summary>
    public string CodeLine => IsKnown ? Code : "";

    public string AutomationId => "Priv_" + Code;

    /// <summary>Попадает под «Выдать все»: известное право, не запрет.</summary>
    internal bool IsGrantable => IsKnown && !RoleCatalog.IsRestriction(Code);

    public bool IsGranted
    {
        get => _isGranted;
        set
        {
            if (!_role.CanEdit) return;
            if (SetProperty(ref _isGranted, value)) _role.OnPrivilegeToggled(this);
        }
    }
}

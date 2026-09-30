using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Server.Config;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using Kind = eViSTool.Core.Server.Config.ConfigValueKind;

namespace eViSTool.App.ViewModels;

/// <summary>Тексты полей конфига: их ключи собираются из имён полей, и в словаре их может не быть.</summary>
internal static class ConfigTexts
{
    /// <summary>Перевод по ключу; null — такого ключа в словаре нет (Loc тогда возвращает сам ключ).</summary>
    public static string? Find(string key)
    {
        if (key.Length == 0) return null;
        var text = Loc.T(key);
        return text == key ? null : text;
    }
}

/// <summary>
/// Поле serverconfig.json в редакторе. Правка сразу пишется в документ, если ввод разобрался; если нет — документ
/// не трогается, а поле показывает ошибку. «Изменено» — значение отличается от загруженного.
/// </summary>
public abstract partial class ConfigFieldViewModel : ObservableObject
{
    private readonly Action<ConfigFieldViewModel> _changed;
    private string? _errorKey;
    private object?[] _errorArgs = [];

    protected ConfigFieldViewModel(ServerConfigDocument doc, ConfigFieldSpec spec, Action<ConfigFieldViewModel> changed)
    {
        Doc = doc;
        Spec = spec;
        _changed = changed;
        Original = doc.Get(spec.Path)?.DeepClone() ?? JValue.CreateNull();
        ReadTexts();
    }

    protected ServerConfigDocument Doc { get; }

    /// <summary>Значение на момент загрузки (сохранения) — с ним сравнивается текущее.</summary>
    protected JToken Original { get; }

    public ConfigFieldSpec Spec { get; }

    /// <summary>Путь поля в JSON ("Port", "WorldConfig.WorldName") — по нему админ узнаёт поле из вики.</summary>
    public string Path => Spec.Path;

    public bool IsWorldCreationOnly => Spec.WorldCreationOnly;
    public bool IsLegacy => Spec.Legacy;

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _hint = "";

    /// <summary>Подпись не совпадает с путём — путь показываем отдельной строкой.</summary>
    [ObservableProperty] private bool _showPath;

    [ObservableProperty] private bool _isModified;
    [ObservableProperty] private string _error = "";

    /// <summary>Сервер работает — поле только для чтения.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    private bool _isReadOnly;

    /// <summary>Поиск на «Дополнительно» скрывает неподходящие поля.</summary>
    [ObservableProperty] private bool _isVisible = true;

    public bool IsEditable => !IsReadOnly;
    public bool HasError => _errorKey is not null;

    public static ConfigFieldViewModel Create(ServerConfigDocument doc, ConfigFieldSpec spec, Action<ConfigFieldViewModel> changed) =>
        spec.Kind switch
        {
            Kind.Bool => new ConfigBoolFieldViewModel(doc, spec, changed),
            Kind.Choice => new ConfigChoiceFieldViewModel(doc, spec, changed),
            Kind.ReadOnly => new ConfigReadOnlyFieldViewModel(doc, spec, changed),
            Kind.Path => new ConfigPathFieldViewModel(doc, spec, changed),
            Kind.Multiline or Kind.StringList or Kind.Json => new ConfigMultilineFieldViewModel(doc, spec, changed),
            Kind.Integer or Kind.Decimal => new ConfigNumberFieldViewModel(doc, spec, changed),
            _ => new ConfigTextFieldViewModel(doc, spec, changed),
        };

    /// <summary>Сменился язык: подпись, подсказка и текст ошибки — заново.</summary>
    public virtual void RefreshTexts()
    {
        ReadTexts();
        Error = _errorKey is null ? "" : Loc.T(_errorKey, _errorArgs);
    }

    private void ReadTexts()
    {
        Label = (Spec.Known ? ConfigTexts.Find(Spec.LabelKey) : null) ?? Spec.Name;
        Hint = ConfigTexts.Find(Spec.HintKey) ?? "";
        ShowPath = Label != Spec.Path;
    }

    /// <summary>Поиск по подписи и имени поля.</summary>
    public bool Matches(string search) =>
        search.Length == 0
        || Label.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || Spec.Path.Contains(search, StringComparison.OrdinalIgnoreCase);

    protected void SetError(string? key, object?[] args)
    {
        _errorKey = key;
        _errorArgs = args;
        Error = key is null ? "" : Loc.T(key, args);
    }

    /// <summary>Записать значение в документ (если оно другое), пересчитать «изменено» и сообщить редактору.</summary>
    protected void Commit(JToken? token)
    {
        if (token is not null && !JToken.DeepEquals(Doc.Get(Spec.Path), token)) Doc.Set(Spec.Path, token);
        // ввод с ошибкой в документ не попал, но поле уже не такое, как было
        IsModified = HasError || !JToken.DeepEquals(Doc.Get(Spec.Path), Original);
        _changed(this);
    }
}

/// <summary>Однострочный текст.</summary>
public partial class ConfigTextFieldViewModel : ConfigFieldViewModel
{
    [ObservableProperty] private string _text;

    public ConfigTextFieldViewModel(ServerConfigDocument doc, ConfigFieldSpec spec, Action<ConfigFieldViewModel> changed)
        : base(doc, spec, changed) =>
        _text = ConfigValueCodec.ToText(Original, spec);

    partial void OnTextChanged(string value)
    {
        // с исходным значением: пока текст не тронут, кодек возвращает токен как был ("" не превращается в null)
        if (ConfigValueCodec.TryParse(value, Spec, Original, out var token, out var errorKey, out var errorArgs))
        {
            SetError(null, []);
            Commit(token ?? JValue.CreateNull());
        }
        else
        {
            SetError(errorKey, errorArgs);
            Commit(null);
        }
    }
}

/// <summary>Целое или дробное число — то же поле ввода, но узкое.</summary>
public sealed class ConfigNumberFieldViewModel(ServerConfigDocument doc, ConfigFieldSpec spec, Action<ConfigFieldViewModel> changed)
    : ConfigTextFieldViewModel(doc, spec, changed);

/// <summary>Многострочный текст, список строк (по одной в строке) или сырой JSON.</summary>
public sealed class ConfigMultilineFieldViewModel(ServerConfigDocument doc, ConfigFieldSpec spec, Action<ConfigFieldViewModel> changed)
    : ConfigTextFieldViewModel(doc, spec, changed)
{
    public bool IsJson => Spec.Kind == Kind.Json;
    public bool IsList => Spec.Kind == Kind.StringList;
}

/// <summary>Путь к файлу с кнопкой «Обзор…» (файл сохранения мира).</summary>
public sealed partial class ConfigPathFieldViewModel(ServerConfigDocument doc, ConfigFieldSpec spec, Action<ConfigFieldViewModel> changed)
    : ConfigTextFieldViewModel(doc, spec, changed)
{
    private const string WorldExtension = ".vcdbs";

    [RelayCommand]
    private void Browse()
    {
        // «сохранить как»: файла мира может ещё не быть — сервер создаст его при запуске
        var world = Spec.Name == "SaveFileLocation";
        var dlg = new SaveFileDialog
        {
            Title = Label,
            OverwritePrompt = false,
            Filter = world ? Loc.T("srvcfg.worldFileFilter") + $" (*{WorldExtension})|*{WorldExtension}" : Loc.T("srvcfg.anyFileFilter") + " (*.*)|*.*",
            DefaultExt = world ? WorldExtension : "",
            AddExtension = world,
        };
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Text.Trim());
            if (Directory.Exists(dir))
            {
                dlg.InitialDirectory = dir;
                dlg.FileName = System.IO.Path.GetFileName(Text.Trim());
            }
        }
        catch (ArgumentException)
        {
            // в поле не путь — диалог откроется там, где был в прошлый раз
        }

        if (dlg.ShowDialog() == true) Text = dlg.FileName;
    }
}

/// <summary>Переключатель вкл/выкл.</summary>
public sealed partial class ConfigBoolFieldViewModel : ConfigFieldViewModel
{
    [ObservableProperty] private bool _value;

    public ConfigBoolFieldViewModel(ServerConfigDocument doc, ConfigFieldSpec spec, Action<ConfigFieldViewModel> changed)
        : base(doc, spec, changed) =>
        _value = Original is JValue { Value: true };

    partial void OnValueChanged(bool value) => Commit(new JValue(value));
}

/// <summary>Вариант выпадающего списка: подпись и то, что запишется в файл.</summary>
public sealed partial class ConfigChoiceOption : ObservableObject
{
    private readonly string _labelKey;

    public ConfigChoiceOption(string value, string labelKey)
    {
        Value = value;
        _labelKey = labelKey;
        _label = Title(value, labelKey);
    }

    public string Value { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Code))]
    private string _label;

    /// <summary>Значение рядом с подписью — когда подпись его не повторяет (числа и коды), чтобы было видно, что запишется.</summary>
    public string Code => Label == Value ? "" : Value;

    public void RefreshTexts() => Label = Title(Value, _labelKey);

    private static string Title(string value, string labelKey) =>
        ConfigTexts.Find(labelKey) ?? (value.Length == 0 ? Loc.T("srvcfg.choiceEmpty") : value);

    public override string ToString() => Label;
}

/// <summary>Выбор из списка.</summary>
public sealed class ConfigChoiceFieldViewModel : ConfigFieldViewModel
{
    private ConfigChoiceOption? _selected;

    public ConfigChoiceFieldViewModel(ServerConfigDocument doc, ConfigFieldSpec spec, Action<ConfigFieldViewModel> changed)
        : base(doc, spec, changed)
    {
        var options = spec.Choices.Select(c => new ConfigChoiceOption(c.Value, c.LabelKey)).ToList();
        var current = ConfigValueCodec.ToText(Original, spec);
        _selected = options.FirstOrDefault(o => o.Value == current);
        if (_selected is null)
        {
            // схема обычно сама добавляет значение из файла к вариантам; если нет — не теряем его
            _selected = new ConfigChoiceOption(current, "");
            options.Add(_selected);
        }
        Options = options;
    }

    /// <summary>Список не пересоздаётся: при замене ItemsSource выпадающий список сбросил бы выбор.</summary>
    public IReadOnlyList<ConfigChoiceOption> Options { get; }

    public ConfigChoiceOption? Selected
    {
        get => _selected;
        set
        {
            if (value is null || ReferenceEquals(value, _selected)) return;
            _selected = value;
            OnPropertyChanged();
            if (ConfigValueCodec.TryParse(value.Value, Spec, Original, out var token, out var errorKey, out var errorArgs))
            {
                SetError(null, []);
                Commit(token ?? JValue.CreateNull());
            }
            else
            {
                SetError(errorKey, errorArgs);
                Commit(null);
            }
        }
    }

    public override void RefreshTexts()
    {
        base.RefreshTexts();
        foreach (var option in Options) option.RefreshTexts();
    }
}

/// <summary>Служебное поле: сервер пишет его сам. Текст можно выделить и скопировать.</summary>
public sealed class ConfigReadOnlyFieldViewModel : ConfigFieldViewModel
{
    public ConfigReadOnlyFieldViewModel(ServerConfigDocument doc, ConfigFieldSpec spec, Action<ConfigFieldViewModel> changed)
        : base(doc, spec, changed) =>
        Text = Original.Type == JTokenType.Null ? "null" : ConfigValueCodec.ToText(Original, spec);

    public string Text { get; }
}

/// <summary>Группа полей — карточка с заголовком.</summary>
public sealed partial class ConfigGroupViewModel : ObservableObject
{
    private readonly string _code;
    private readonly string _titleKey;

    public ConfigGroupViewModel(string code, string titleKey, IReadOnlyList<ConfigFieldViewModel> fields)
    {
        _code = code;
        _titleKey = titleKey;
        Fields = fields;
        RefreshTexts();
    }

    public IReadOnlyList<ConfigFieldViewModel> Fields { get; }

    [ObservableProperty] private string _title = "";

    /// <summary>В группе не осталось полей, подходящих под поиск, — карточка скрыта.</summary>
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>Нет перевода названия группы — показываем её код с заглавной («Network»), а не ключ словаря.</summary>
    public void RefreshTexts() =>
        Title = ConfigTexts.Find(_titleKey) ?? (_code.Length == 0 ? "" : char.ToUpperInvariant(_code[0]) + _code[1..]);

    public void ApplySearch(string search)
    {
        foreach (var field in Fields) field.IsVisible = field.Matches(search);
        IsVisible = Fields.Any(f => f.IsVisible);
    }
}

// ---------- настройки мира (WorldConfig.WorldConfiguration) ----------

/// <summary>Известный игре ключ настройки мира — пункт выпадающего списка ключей.</summary>
public sealed partial class WorldSettingKeyOption : ObservableObject
{
    public WorldSettingKeyOption(WorldSettingInfo info)
    {
        Info = info;
        RefreshTexts();
    }

    public WorldSettingInfo Info { get; }
    public string Key => Info.Key;

    /// <summary>Код категории: по нему список сгруппирован (подпись группы меняется с языком, код — нет).</summary>
    public string Category => Info.Category;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyNote))]
    private string _label = "";

    [ObservableProperty] private string _categoryLabel = "";

    /// <summary>Ключ рядом с названием — когда название его не повторяет.</summary>
    public string KeyNote => Label == Key ? "" : Key;

    public void RefreshTexts()
    {
        Label = ConfigTexts.Find(Info.LabelKey) ?? Info.Key;
        CategoryLabel = ConfigTexts.Find(Info.CategoryKey) ?? Info.Category;
    }

    /// <summary>В поле ввода редактируемого списка попадает ключ.</summary>
    public override string ToString() => Key;
}

/// <summary>Допустимое значение настройки мира: что запишется и как это называется в игре.</summary>
public sealed record WorldSettingValueOption(string Value, string Label)
{
    public string ValueNote => Label == Value ? "" : Value;
    public override string ToString() => Label;
}

public enum WorldSettingProblem { None, NoKey, Duplicate }

/// <summary>Строка таблицы «Настройки мира»: ключ и значение (оба — строки, как в файле).</summary>
public sealed partial class WorldSettingRowViewModel : ObservableObject
{
    private readonly Action<WorldSettingRowViewModel> _changed;
    private readonly Action<WorldSettingRowViewModel> _remove;
    private WorldSettingInfo? _info;
    private string _value;

    // значение подставлено из «по умолчанию» и пользователем не тронуто: сменится ключ — сменится и оно
    private bool _autoValue;

    public WorldSettingRowViewModel(string key, string value, ICollectionView keyOptions,
        Action<WorldSettingRowViewModel> changed, Action<WorldSettingRowViewModel> remove)
    {
        _key = key;
        _value = value;
        KeyOptions = keyOptions;
        _changed = changed;
        _remove = remove;
        ResolveKey();
    }

    /// <summary>Известные ключи (общий список на все строки), сгруппированы по категориям.</summary>
    public ICollectionView KeyOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyAutomationId), nameof(ValueAutomationId), nameof(RemoveAutomationId))]
    private string _key;

    public string Value
    {
        get => _value;
        set
        {
            if (!SetProperty(ref _value, value)) return;
            _autoValue = false;
            OnPropertyChanged(nameof(SelectedOption));
            _changed(this);
        }
    }

    /// <summary>Для Choice/Bool из каталога — выпадающий список значений; иначе пусто и значение правится текстом.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValueOptions))]
    private IReadOnlyList<WorldSettingValueOption> _valueOptions = [];

    public bool HasValueOptions => ValueOptions.Count > 0;

    /// <summary>Выбранное значение. null от списка (его только что заменили) — не выбор пользователя, пропускаем.</summary>
    public WorldSettingValueOption? SelectedOption
    {
        get => ValueOptions.FirstOrDefault(o => o.Value == Value);
        set
        {
            if (value is not null && value.Value != Value) Value = value.Value;
        }
    }

    /// <summary>Как настройка называется в игре (если игра её знает и есть перевод).</summary>
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private string _defaultText = "";

    /// <summary>Ключа нет в каталоге базовой игры: не ошибка (может быть от мода), но стоит проверить на опечатку.</summary>
    [ObservableProperty] private bool _isUnknown;

    /// <summary>Игра читает настройку только при создании мира.</summary>
    [ObservableProperty] private bool _isWorldCreationOnly;

    [ObservableProperty] private bool _isModified;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(HasError))]
    private WorldSettingProblem _problem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    private bool _isReadOnly;

    public bool IsEditable => !IsReadOnly;
    public bool HasError => Problem != WorldSettingProblem.None;

    public string Error => Problem switch
    {
        WorldSettingProblem.NoKey => Loc.T("srvcfg.wsNoKey"),
        WorldSettingProblem.Duplicate => Loc.T("srvcfg.wsDuplicate"),
        _ => "",
    };

    public string KeyAutomationId => "WorldSettingKey." + Key.Trim();
    public string ValueAutomationId => "WorldSetting." + Key.Trim();
    public string RemoveAutomationId => "WorldSettingRemove." + Key.Trim();

    partial void OnKeyChanged(string value)
    {
        // у только что выбранной настройки значения ещё нет — подставляем то, что игра берёт по умолчанию
        // (ключ при наборе автодополняется, поэтому подставленное меняется вместе с ним, пока его не тронули)
        if (_value.Length == 0 || _autoValue)
        {
            _autoValue = true;
            _value = WorldSettingCatalog.Find(value.Trim())?.Default ?? "";
            OnPropertyChanged(nameof(Value));
        }
        ResolveKey();
        _changed(this);
    }

    [RelayCommand]
    private void Remove() => _remove(this);

    /// <summary>Сменился язык: названия и подсказки — заново.</summary>
    public void RefreshTexts()
    {
        ResolveKey();
        OnPropertyChanged(nameof(Error));
    }

    private void ResolveKey()
    {
        var key = Key.Trim();
        _info = key.Length == 0 ? null : WorldSettingCatalog.Find(key);
        // пока каталог пуст, «неизвестны» все ключи — тогда и помечать нечего
        IsUnknown = key.Length > 0 && _info is null && WorldSettingCatalog.All.Count > 0;
        IsWorldCreationOnly = _info?.OnlyAtWorldCreation == true;

        var options = new List<WorldSettingValueOption>();
        if (_info is { } info)
        {
            IReadOnlyList<string> values = info.Type switch
            {
                WorldSettingType.Bool => info.Values.Count > 0 ? info.Values : ["true", "false"],
                WorldSettingType.Choice => info.Values,
                _ => [],
            };
            options.AddRange(values.Select(v => new WorldSettingValueOption(v, ValueLabel(info, v))));
            // значение из файла, которого нет среди известных, не теряем
            if (options.Count > 0 && Value.Length > 0 && options.All(o => o.Value != Value))
                options.Add(new WorldSettingValueOption(Value, Value));
        }
        ValueOptions = options;
        OnPropertyChanged(nameof(SelectedOption));

        Title = _info is null ? "" : ConfigTexts.Find(_info.LabelKey) ?? "";
        Hint = _info is null ? "" : ConfigTexts.Find(_info.HintKey) ?? "";
        DefaultText = _info is { Default: { Length: > 0 } value } known ? Loc.T("srvcfg.wsDefault", ValueLabel(known, value, withValue: true)) : "";
    }

    private static string ValueLabel(WorldSettingInfo info, string value, bool withValue = false)
    {
        var label = ConfigTexts.Find(info.ValueLabelKey(value));
        return label is null || label == value ? value : withValue ? $"{label} ({value})" : label;
    }
}

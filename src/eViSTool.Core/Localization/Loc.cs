using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json;

namespace eViSTool.Core.Localization;

/// <summary>
/// Переводы интерфейса. Словари — плоские JSON (ключ → текст) в Lang/*.json, вшиты в сборку.
/// Нет ключа в выбранном языке — берётся английский, нет и там — показывается сам ключ (сразу видно, что забыли).
/// Для WPF: индексатор + PropertyChanged("Item[]") — привязки обновляются при смене языка на лету.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string DefaultLanguage = "en";

    public static Loc Instance { get; } = new();

    /// <summary>Языки, для которых есть словарь: код → самоназвание.</summary>
    public static IReadOnlyList<(string Code, string Name)> Available { get; } = [("en", "English"), ("ru", "Русский")];

    private Dictionary<string, string> _current = new();
    private readonly Dictionary<string, string> _fallback;

    public string Language { get; private set; } = DefaultLanguage;

    private Loc()
    {
        _fallback = Load(DefaultLanguage);
        _current = _fallback;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Get(key);

    public void SetLanguage(string? code)
    {
        code = Available.Any(a => a.Code == code) ? code! : DefaultLanguage;
        Language = code;
        _current = code == DefaultLanguage ? _fallback : Load(code);
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(code);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    /// <summary>Перевод по ключу; {0}, {1}… подставляются из args.</summary>
    public static string T(string key, params object?[] args)
    {
        var text = Instance.Get(key);
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
    }

    private string Get(string key) =>
        _current.TryGetValue(key, out var s) ? s : _fallback.TryGetValue(key, out var f) ? f : key;

    private static Dictionary<string, string> Load(string code)
    {
        var asm = typeof(Loc).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith($".Lang.{code}.json", StringComparison.OrdinalIgnoreCase));
        if (name is null) return new();
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd()) ?? new();
    }

    /// <summary>Все ключи словаря (для теста «все ключи переведены»).</summary>
    public static IReadOnlyCollection<string> Keys(string code) => Load(code).Keys;
}

using Avalonia.Data;
using Avalonia.Markup.Xaml;
using eViSTool.Core.Localization;

namespace eViSTool.Desktop;

/// <summary>
/// Перевод в разметке: Text="{local:Tr mods.check}". Это привязка к Loc.Instance[ключ], поэтому текст меняется сразу
/// при переключении языка — как и в окне на WPF.
/// </summary>
public sealed class Tr(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
}

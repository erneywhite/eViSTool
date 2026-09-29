using System.Windows.Data;
using System.Windows.Markup;
using eViSTool.Core.Localization;

namespace eViSTool.App;

/// <summary>
/// Перевод в разметке: Text="{l:Tr mods.check}". Это привязка к Loc.Instance[ключ],
/// поэтому текст меняется сразу при переключении языка.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class Tr(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

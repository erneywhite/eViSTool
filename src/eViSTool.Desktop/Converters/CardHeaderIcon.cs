using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using eViSTool.Presentation;

namespace eViSTool.Desktop.Converters;

/// <summary>
/// Иконка из файла мода для шапки карточки (docs/design.md, раздел 7). <c>ctl:ModIconImage.Source</c> читает архив, даже
/// если картинка спрятана, поэтому, когда логотип известен с открытия карточки (<c>ModCardViewModel.IconWaitsForLogo</c>),
/// иконку не отдаём, пока он не подведёт: файл не открывается и 256 px не декодируются зря. Логотип не загрузился —
/// иконка встаёт на его место.
/// <code>
/// &lt;MultiBinding Converter="{x:Static conv:CardHeaderIcon.FileUnlessLogo}"&gt;
///     &lt;Binding Path="FileIcon" /&gt; &lt;Binding Path="IconWaitsForLogo" /&gt;
///     &lt;Binding ElementName="Logo1" Path="(ctl:RemoteImage.Failed)" /&gt;
/// &lt;/MultiBinding&gt;
/// </code>
/// </summary>
public static class CardHeaderIcon
{
    public static readonly IMultiValueConverter FileUnlessLogo = new Converter();

    /// <summary>Иконка из файла, если она не ждёт логотипа или логотип не загрузился; иначе null.</summary>
    public static ModIconSource? Pick(ModIconSource? file, bool waitsForLogo, bool logoFailed) =>
        !waitsForLogo || logoFailed ? file : null;

    private sealed class Converter : IMultiValueConverter
    {
        // пока какая-то из привязок не дошла до значения (UnsetValue), не знаем, ждать ли логотипа, — иконку не просим:
        // иначе архив открылся бы на миг зря
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
            values.Count < 3 || values.Any(v => v is UnsetValueType or BindingNotification) ? null
                : Pick(values[0] as ModIconSource, values[1] is true, values[2] is true);
    }
}

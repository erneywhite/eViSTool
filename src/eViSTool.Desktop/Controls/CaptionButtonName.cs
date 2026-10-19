using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Имя кнопки своей шапки окна для экранного диктора — из темы (Themes/ForestWindowChrome.axaml), на языке программы и
/// по состоянию окна: «Развернуть» или «Восстановить». Avalonia, собрав шаблон шапки (WindowDrawnDecorations), пишет
/// кнопкам свои английские имена («Minimize», «Maximize», «Close») локальным значением, а оно сильнее стилей темы.
/// Кнопка с <c>ctl:CaptionButtonName.FromTheme="True"</c> сбрасывает его, когда попадает в окно: слои шапки
/// подключаются к окну уже после сборки шаблона, так что сброс всегда позже записи.
/// </summary>
public static class CaptionButtonName
{
    public static readonly AttachedProperty<bool> FromThemeProperty =
        AvaloniaProperty.RegisterAttached<Button, bool>("FromTheme", typeof(CaptionButtonName));

    static CaptionButtonName()
    {
        // обработчик своего же свойства: разметка трогает FromThemeProperty раньше, чем ставит его, — он уже на месте.
        // Стиль темы мог дойти до кнопки, когда она уже в окне (стили применяются и при раскладке), — тогда сразу
        FromThemeProperty.Changed.AddClassHandler<Button>((button, e) =>
        {
            button.AttachedToVisualTree -= ClearName;
            if (!e.GetNewValue<bool>()) return;
            button.AttachedToVisualTree += ClearName;
            if (button.IsAttachedToVisualTree()) ClearName(button, null);
        });
    }

    private static void ClearName(object? sender, VisualTreeAttachmentEventArgs? e) =>
        ((Button)sender!).ClearValue(AutomationProperties.NameProperty);

    public static bool GetFromTheme(Button button) => button.GetValue(FromThemeProperty);
    public static void SetFromTheme(Button button, bool value) => button.SetValue(FromThemeProperty, value);
}

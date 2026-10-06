using System.Windows;

namespace eViSTool.App;

/// <summary>
/// Вопросы и предупреждения пользователю. Через этот класс — чтобы тесты моделей окна могли отвечать за пользователя
/// (подменяют <see cref="Ask"/> и <see cref="Warn"/>), а не ждали настоящее окно сообщения.
/// </summary>
public static class Dialogs
{
    public static Func<string, MessageBoxButton, MessageBoxResult> Ask { get; set; } = (text, buttons) =>
        MessageBox.Show(Application.Current.MainWindow!, text, "eViSTool", buttons, MessageBoxImage.Question);

    public static Action<string> Warn { get; set; } = text =>
        MessageBox.Show(Application.Current.MainWindow!, text, "eViSTool", MessageBoxButton.OK, MessageBoxImage.Warning);
}

using Avalonia.Interactivity;
using eViSTool.Core.Localization;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// Пароль сервера — перед входом или добавлением в избранное. Считать здесь нечего, поэтому модели нет: заголовок с
/// именем сервера и ответ — введённый пароль. «Готово» с пустым полем отвечает пустой строкой, «Отмена», Escape и
/// крестик — null.
/// </summary>
public partial class ServerPasswordWindow : DialogWindow
{
    private string _server = "";

    public ServerPasswordWindow() => InitializeComponent();

    /// <summary>Сервер, для которого спрашивают пароль: его имя — в заголовке окна.</summary>
    public string Server
    {
        get => _server;
        set
        {
            _server = value;
            Heading.Text = Loc.T("browse.passwordFor", value);
        }
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close(Box.Text ?? "");

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}

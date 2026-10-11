using eViSTool.Desktop.Dialogs;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>Пароль сервера перед входом или добавлением в избранное; null — отказались, "" — «Готово» с пустым полем.</summary>
    public string? AskServerPassword(string server) =>
        new ServerPasswordWindow { Server = server }.ShowModal<string>();
}

using Avalonia.Interactivity;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// Подключение к серверу у хостера по FTP или SFTP: адрес, порт, шифрование FTP или способ входа SFTP (пароль или файл
/// ключа), логин и пароль, проверка связи. Разбор адреса и сама проверка — в <see cref="HostedConnectViewModel"/>
/// (DataContext); здесь — раскрытие «Дополнительно» и ответ окна: подключение по «ОК».
/// </summary>
public partial class HostedConnectWindow : DialogWindow
{
    public HostedConnectWindow()
    {
        InitializeComponent();
        // закрыли, пока шла проверка, — её ответ уже некому показывать
        Closed += (_, _) => Model?.CancelCheck();
    }

    private HostedConnectViewModel? Model => DataContext as HostedConnectViewModel;

    /// <summary>Ушли из поля адреса: ссылка раскладывается по полям, остаётся один хост.</summary>
    private void Address_LostFocus(object? sender, RoutedEventArgs e) => Model?.CommitAddress();

    private void Advanced_Click(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model) model.IsAdvancedOpen = !model.IsAdvancedOpen;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (Model?.Accept() is { } connection) Close(connection);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}

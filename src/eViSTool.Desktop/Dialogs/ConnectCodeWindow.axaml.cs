using System.Runtime.InteropServices;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// Вставить код подключения к серверу на другом компьютере и проверить связь. Код не показывается — поле скрытое;
/// видно только, куда он ведёт (адрес и порт). Разбор и проверка — в <see cref="ConnectCodeViewModel"/> (DataContext),
/// здесь — буфер обмена и ответ окна: разобранный код по «ОК».
/// </summary>
public partial class ConnectCodeWindow : DialogWindow
{
    public ConnectCodeWindow()
    {
        InitializeComponent();
        // закрыли, пока шла проверка, — её ответ уже некому показывать
        Closed += (_, _) => Model?.CancelCheck();
    }

    private ConnectCodeViewModel? Model => DataContext as ConnectCodeViewModel;

    /// <summary>Код из буфера — в скрытое поле, не показывая его.</summary>
    private async void Paste_Click(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || Clipboard is not { } clipboard) return;
        try
        {
            if (await clipboard.TryGetTextAsync() is { } text) model.Text = text.Trim();
        }
        catch (Exception ex) when (ex is TimeoutException or ExternalException)
        {
            // буфер занят другой программой — можно вставить Ctrl+V
        }
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (Model?.Code is { } code) Close(code);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// Поле для секрета: TextBox с <see cref="TextBox.PasswordChar"/>, который не отдаёт свой текст специальным
/// возможностям системы. Обычный TextBox Avalonia отдаёт Text любому клиенту UI Automation даже с точками вместо букв,
/// а PasswordBox в окне на WPF — нет. Вид — как у обычного поля (та же тема).
/// </summary>
public sealed class SecretTextBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    protected override AutomationPeer OnCreateAutomationPeer() => new SecretPeer(this);

    /// <summary>Поле ввода для специальных возможностей, но без значения (нет IValueProvider).</summary>
    private sealed class SecretPeer(Control owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;
    }
}

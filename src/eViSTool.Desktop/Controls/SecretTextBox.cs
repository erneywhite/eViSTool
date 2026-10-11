using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace eViSTool.Desktop.Controls;

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

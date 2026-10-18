using Avalonia.Controls;

namespace eViSTool.Desktop.Views;

/// <summary>
/// Раздел «Настройки»: общее для всей программы — вид, обновления, моды, оповещения, данные программы. Всё про
/// конкретный профиль — в «Профилях» (<see cref="ProfilesView"/>). DataContext — MainViewModel.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}

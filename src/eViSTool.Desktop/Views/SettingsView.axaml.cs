using Avalonia.Controls;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Views;

/// <summary>Раздел «Настройки»: профили, папки игры, поведение программы. DataContext — MainViewModel.</summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    // список серверов для «Сервера по умолчанию» — свежий при каждом открытии (избранное игры могли поменять)
    private void DefaultServer_DropDownOpened(object? sender, EventArgs e) => (DataContext as MainViewModel)?.NotifyEditedDefaultServer();
}

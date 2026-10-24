using Avalonia.Controls;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Views;

/// <summary>
/// Раздел «Профили»: список профилей и действия с ними, карточка выбранного (папки или подключение, сервер по
/// умолчанию), связанные профили, копии миров. DataContext — MainViewModel.
/// </summary>
public partial class ProfilesView : UserControl
{
    public ProfilesView() => InitializeComponent();

    // профили переставили (порядок «Последняя игра») — список не теряет открытый профиль
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm) Controls.KeepSelection.Follow(ProfileList, vm.SortedProfiles, () => vm.EditedProfile);
    }

    // список серверов для «Сервера по умолчанию» — свежий при каждом открытии (избранное игры могли поменять)
    private void DefaultServer_DropDownOpened(object? sender, EventArgs e) => (DataContext as MainViewModel)?.NotifyEditedDefaultServer();
}

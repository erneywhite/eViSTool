using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Views;

/// <summary>
/// Раздел «Профили»: список профилей и действия с ними, карточка выбранного (папки или подключение, сервер по
/// умолчанию), связанные профили, копии миров. DataContext — MainViewModel.
/// </summary>
public partial class ProfilesView : UserControl
{
    private MainViewModel? _vm;

    public ProfilesView() => InitializeComponent();

    // профили переставили (порядок «Последняя игра») — список не теряет открытый профиль
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= Vm_PropertyChanged;
        _vm = DataContext as MainViewModel;
        if (_vm is null) return;
        Controls.KeepSelection.Follow(ProfileList, _vm.SortedProfiles, () => _vm.EditedProfile);
        _vm.PropertyChanged += Vm_PropertyChanged;
    }

    /// <summary>
    /// Варианты «Сервера по умолчанию» пересобрали (смена профиля, открытие списка) — выпадающий список при этом теряет
    /// выбор, а привязка не переотдаёт ему тот же ключ. Модель пустой выбор пропускает, а выбор здесь возвращается,
    /// когда список закончит пересборку.
    /// </summary>
    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.EditedDefaultServerOptions)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm is null) return;
            var field = this.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == "DefaultServer");
            if (field is not null && !Equals(field.SelectedValue, _vm.EditedDefaultServer)) field.SelectedValue = _vm.EditedDefaultServer;
        });
    }

    // список серверов для «Сервера по умолчанию» — свежий при каждом открытии (избранное игры могли поменять)
    private void DefaultServer_DropDownOpened(object? sender, EventArgs e) => _vm?.NotifyEditedDefaultServer();
}

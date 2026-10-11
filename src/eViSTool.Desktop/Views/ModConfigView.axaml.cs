using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Views;

/// <summary>
/// Вкладка «Настройки модов»: файлы ModConfig профиля слева, выбранный файл справа — формой или текстом.
/// Вопрос о несохранённых правках при уходе с вкладки и закрытии окна задаёт модель (ConfirmLeave, ConfirmExit).
/// </summary>
public partial class ModConfigView : UserControl
{
    private ModConfigViewModel? _vm;

    public ModConfigView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as ModConfigViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
    }

    // Модель может отменить выбор вида прямо во время записи: «Форма», а текст сейчас не JSON — остаётся «Текст».
    // Привязка Avalonia не слышит оповещений, пока сама пишет в модель, и чип «Форма» остался бы выбранным над текстом
    // (привязка WPF после записи перечитывает значение сама). Поэтому после записи сверяем чипы с моделью.
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModConfigViewModel.IsFormMode)) Dispatcher.UIThread.Post(SyncViewChips);
    }

    private void SyncViewChips()
    {
        if (_vm is null) return;
        if (FormChip.IsChecked != _vm.IsFormMode) FormChip.SetCurrentValue(ToggleButton.IsCheckedProperty, _vm.IsFormMode);
        if (TextChip.IsChecked != _vm.IsTextMode) TextChip.SetCurrentValue(ToggleButton.IsCheckedProperty, _vm.IsTextMode);
    }
}

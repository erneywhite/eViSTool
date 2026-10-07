using System.Windows;
using System.Windows.Controls;
using eViSTool.App.ViewModels;

namespace eViSTool.App;

public partial class ModConfigView : UserControl
{
    private Window? _window;

    public ModConfigView()
    {
        InitializeComponent();
        // окно закрывают с несохранёнными правками конфига — спросить
        Loaded += (_, _) =>
        {
            if (_window is not null || Window.GetWindow(this) is not { } window) return;
            _window = window;
            window.Closing += (_, e) =>
            {
                if (!e.Cancel && DataContext is ModConfigViewModel vm && !vm.ConfirmLeave()) e.Cancel = true;
            };
        };
    }
}

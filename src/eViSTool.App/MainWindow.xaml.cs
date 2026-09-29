using System.Windows;
using eViSTool.App.ViewModels;

namespace eViSTool.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    // размер окна — как в прошлый раз (но не больше экрана)
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var l = Vm.Layout;
        var area = SystemParameters.WorkArea;
        Width = Math.Min(l.Width, area.Width);
        Height = Math.Min(l.Height, area.Height);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
        if (l.Maximized) WindowState = WindowState.Maximized;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var l = Vm.Layout;
        l.Maximized = WindowState == WindowState.Maximized;
        var bounds = l.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        l.Width = bounds.Width;
        l.Height = bounds.Height;
        Vm.SaveSettings();
    }

    // перетаскивание zip-архивов из Проводника в список модов
    private void ModsGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void ModsGrid_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            Vm.Mods.AddFiles(files);
    }
}

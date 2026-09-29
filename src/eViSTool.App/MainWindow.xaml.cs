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

    // тёмный заголовок окна в цвет боковой колонки (Windows 11; на Windows 10 — просто тёмный)
    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int dark = 1, caption = 0x001D2319 /* #19231D в формате 0x00BBGGRR */, text = 0x00DDE9E7;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36 /* DWMWA_TEXT_COLOR */, ref text, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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

    // «Модпак ▾»: меню открывается обычным кликом
    private void PackMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } menu } button)
        {
            menu.DataContext = button.DataContext;
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
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

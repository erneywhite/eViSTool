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

    // профили добавляют и переименовывают на ходу, а выпадашка мерит себя по пунктам с прошлого раза:
    // новый пункт оказывался за нижним краем, длинное имя — обрезанным. Перед открытием пункты строятся заново.
    private void ProfileCombo_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox combo) combo.Items.Refresh();
    }

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
        // смену профиля отменили (правки конфига) — вернуть список на текущий профиль, когда он закончит обработку выбора.
        // По индексу: после отказа у списка SelectedItem уже прежний, а SelectedIndex и показ — на отвергнутом пункте,
        // и присвоение того же SelectedItem ничего не меняет
        Vm.ProfileSwitchDeclined += () => Dispatcher.BeginInvoke(() =>
        {
            if (Vm.ActiveProfile is { } active) ProfileCombo.SelectedIndex = Vm.Profiles.IndexOf(active);
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);

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
}

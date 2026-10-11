using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Views;

public partial class MainWindow : Window
{
    private Size _normalSize; // размер до разворота: у Avalonia нет RestoreBounds, запоминаем сами

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => OnOpened();
        Closing += OnClosing;
        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal) _normalSize = ClientSize;
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext!;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainViewModel vm) return;
        vm.PropertyChanged += OnVmPropertyChanged;
        // смену профиля отменили (правки конфига) — вернуть список на текущий профиль, когда он закончит обработку выбора
        vm.ProfileSwitchDeclined += () => Dispatcher.UIThread.Post(() =>
        {
            if (Vm.ActiveProfile is { } active) ProfileCombo.SelectedIndex = Vm.Profiles.IndexOf(active);
        }, DispatcherPriority.Background);
        ShowPage();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedTab)) ShowPage();
    }

    // виден один раздел — по SelectedTab (порядок страниц в разметке = порядок AppTab)
    private void ShowPage()
    {
        var index = (int)Vm.SelectedTab;
        for (var i = 0; i < Pages.Children.Count; i++) Pages.Children[i].IsVisible = i == index;
    }

    // размер окна — как в прошлый раз (но не больше рабочей области экрана)
    private void OnOpened()
    {
        if (OperatingSystem.IsWindows()) DarkCaption();
        var l = Vm.Layout;
        if (Screens.ScreenFromWindow(this) is { } screen)
        {
            var area = screen.WorkingArea.ToRect(screen.Scaling);
            Width = Math.Min(l.Width, area.Width);
            Height = Math.Min(l.Height, area.Height);
            Position = PixelPoint.FromPoint(new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2), screen.Scaling);
        }
        _normalSize = new Size(Width, Height);
        if (l.Maximized) WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        var l = Vm.Layout;
        l.Maximized = WindowState == WindowState.Maximized;
        var size = l.Maximized ? _normalSize : ClientSize;
        l.Width = size.Width;
        l.Height = size.Height;
        Vm.SaveSettings();
    }

    // тёмный заголовок окна в цвет боковой колонки (Windows 11; на Windows 10 — просто тёмный)
    private void DarkCaption()
    {
        if (TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
        int dark = 1, caption = 0x001D2319 /* #19231D в формате 0x00BBGGRR */, text = 0x00DDE9E7;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36 /* DWMWA_TEXT_COLOR */, ref text, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ---------- меню «Играть ▾»: собирается заново при каждом открытии (серверы могли запустить, избранное — поменять)

    private void PlayMenu_Click(object? sender, RoutedEventArgs e)
    {
        Vm.RefreshPlayMenu();
        PlayMenu.IsOpen = true;
    }

    // пункт меню — сыграть там один раз
    private void PlayMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not PlayMenuItemViewModel item) return;
        PlayMenu.IsOpen = false;
        if (Vm.PlayOnCommand.CanExecute(item)) Vm.PlayOnCommand.Execute(item);
    }

    private void FindServer_Click(object? sender, RoutedEventArgs e)
    {
        PlayMenu.IsOpen = false;
        Vm.OpenFindServer();
    }

    // булавка — сервер по умолчанию; меню остаётся открытым, чтобы было видно, что выбралось
    private void PlayMenuPin_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PlayMenuItemViewModel item) Vm.PinPlayTargetCommand.Execute(item);
    }
}

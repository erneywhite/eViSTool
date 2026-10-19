using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using eViSTool.Core.Game;
using eViSTool.Desktop.Controls;
using eViSTool.Desktop.Platform;
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
        // раз в час: вдруг наступила новая неделя или праздник (выбор тот же — файл не перечитывается)
        _backdropClock.Tick += (_, _) => UpdateBackdrop();
        _backdropClock.Start();
        Closed += (_, _) => _backdropClock.Stop();
        // «горизонтали» заглушки плывут, пока окно на экране и в фокусе, а их видно (фона из игры нет)
        Drift = new ContourDrift(this, Contours);
        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal) _normalSize = ClientSize;
            // узкое окно: подпись «Активный профиль» уступает место разделу и кнопкам окна
            ActiveProfileLabel.IsVisible = ClientSize.Width >= 1100;
        };
        SetupChrome();
    }

    // ---------- своя шапка: верхняя полоса окна вместо системного заголовка

    // место под кнопки окна справа от «Играть» (за вычетом правого поля полосы): Windows — 3 × 46, Linux — 3 круга по 24 с зазорами
    private static readonly double CaptionWidth = OperatingSystem.IsWindows() ? 3 * 46 + 12 - 20 : 3 * 24 + 2 * 10 + 15 + 16 - 20;

    private void SetupChrome()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        WindowDecorationsTheme = (ControlTheme)this.FindResource(OperatingSystem.IsWindows() ? "Forest.WindowChromeWin" : "Forest.WindowChromeLinux")!;
        if (OperatingSystem.IsWindows())
        {
            // Windows: полоса и логотип — заголовок окна для системы: перетаскивание, разворот двойным щелчком, Snap,
            // системное меню по правой кнопке
            WindowDecorationProperties.SetElementRole(TopBar, WindowDecorationsElementRole.TitleBar);
            WindowDecorationProperties.SetElementRole(LogoArea, WindowDecorationsElementRole.TitleBar);
        }
        else
        {
            // Linux (X11): роль TitleBar отдала бы нажатие оконному менеджеру целиком и двойной щелчок пропал бы, поэтому сами
            TopBar.PointerPressed += DragArea_PointerPressed;
            LogoArea.PointerPressed += DragArea_PointerPressed;
        }
        ApplyWindowFrame(systemFrame: false);
    }

    // Linux: рамку, тень и уголки для растягивания Avalonia рисует по краям окна, а отступ от них — дело окна
    // (WindowDecorationMargin = рамка + тень, сверху ещё высота шапки, которую занимает наша полоса: её не вычитаем)
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowDecorationMarginProperty && OperatingSystem.IsLinux() && Root is { } root)
        {
            var m = WindowDecorationMargin;
            root.Margin = new Thickness(m.Left, m.Left, m.Right, m.Bottom);
        }
    }

    private void ApplyWindowFrame(bool systemFrame)
    {
        var own = OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && !systemFrame);
        ExtendClientAreaToDecorationsHint = own;
        CaptionSpace.Width = own ? CaptionWidth : 0;
        // под тенью своей рамки на Linux окно прозрачное; фон даёт Root
        if (OperatingSystem.IsLinux() && own) Background = Brushes.Transparent;
        else ClearValue(BackgroundProperty);
    }

    // кнопки и список профилей забирают нажатие себе, сюда доходит только пустое место и подписи
    private void DragArea_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsExtendedIntoWindowDecorations || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            BeginMoveDrag(e);
        e.Handled = true;
    }

    private MainViewModel Vm => (MainViewModel)DataContext!;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainViewModel vm) return;
        vm.PropertyChanged += OnVmPropertyChanged;
        if (OperatingSystem.IsLinux()) ApplyWindowFrame(vm.SystemWindowFrame);
        // смену профиля отменили (правки конфига) — вернуть список на текущий профиль, когда он закончит обработку выбора
        vm.ProfileSwitchDeclined += () => Dispatcher.UIThread.Post(() =>
        {
            if (Vm.ActiveProfile is { } active) ProfileCombo.SelectedIndex = Vm.Profiles.IndexOf(active);
        }, DispatcherPriority.Background);
        ShowPage();
        UpdateBackdrop();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedTab)) ShowPage();
        else if (e.PropertyName == nameof(MainViewModel.SystemWindowFrame) && OperatingSystem.IsLinux()) ApplyWindowFrame(Vm.SystemWindowFrame);
        // папки игры (сменили активный профиль или его папку игры) и галочка «Фон из игры»
        else if (e.PropertyName is nameof(MainViewModel.IconSearchDirs) or nameof(MainViewModel.GameBackground)) UpdateBackdrop();
    }

    // ---------- фон из игры под боковой панелью (docs/design.md, раздел 5)

    private readonly DispatcherTimer _backdropClock = new() { Interval = TimeSpan.FromHours(1) };
    private string? _backdropPath; // показанная заставка; null — заглушка
    private int _backdropRun;      // номер пересчёта: ответ из фона, который опоздал к более свежему, отбрасывается

    /// <summary>Сдвиг «горизонталей» заглушки (видны, когда картинки нет).</summary>
    internal ContourDrift Drift { get; }

    /// <summary>
    /// Выбрать заставку на сегодня (<see cref="GameBackdrop"/>) и показать её: поиск файла и загрузка — в фоне, файл
    /// читается, только если выбор поменялся. Галочка выключена, картинок нет или файл не читается — заглушка.
    /// </summary>
    private async void UpdateBackdrop()
    {
        if (DataContext is not MainViewModel vm) return;
        var run = ++_backdropRun;
        try
        {
            string? path = null;
            if (vm.GameBackground)
            {
                // папки — в порядке IconSearchDirs, но без его FindGameDir на этом потоке: поиск установленной игры
                // ходит по диску (на Linux ещё и разбирает exe), поэтому в фоне вместе с выбором файла
                string?[] profiles = [vm.ActiveProfile?.GameDir, .. vm.Profiles.Select(p => p.GameDir)];
                path = await Task.Run(() => GameBackdrop.Find([.. profiles, GameInstall.FindGameDir()], TimeProvider.System));
                if (run != _backdropRun) return;
            }
            if (path == _backdropPath) return;

            Bitmap? picture = null;
            if (path is not null)
            {
                // под прозрачными местами праздничных заставок — цвет боковой панели
                var underlay = this.FindResource("Forest.Side") is ISolidColorBrush side ? side.Color : Colors.Black;
                picture = await GameBackdropImage.LoadAsync(path, underlay);
                if (run != _backdropRun) return;
            }
            _backdropPath = path;
            Backdrop.Source = picture;
            Root.Classes.Set("backdrop", picture is not null);
        }
        catch (Exception ex)
        {
            // фон — украшение: что бы ни случилось, окно работает дальше с заглушкой
            CrashLog.Write(ex);
            _backdropPath = null;
            Backdrop.Source = null;
            Root.Classes.Set("backdrop", false);
        }
    }

    // виден один раздел — по SelectedTab (порядок страниц в разметке = порядок AppTab). Показанная страница проявляется
    // сама (Motion.RevealChildren у Pages), только прозрачностью и сдвигом. Первый вызов — до показа окна: при запуске
    // страница сразу на месте
    private void ShowPage()
    {
        var index = (int)Vm.SelectedTab;
        for (var i = 0; i < Pages.Children.Count; i++) Pages.Children[i].IsVisible = i == index;
    }

    // размер окна — как в прошлый раз (но не больше рабочей области экрана)
    private void OnOpened()
    {
        WindowCaption.Dark(this);
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
        Vm.SaveOnExit();
        // несохранённые правки конфигурации сервера и конфига мода — спросить; отказались — окно остаётся
        if (!Vm.ConfirmExit()) e.Cancel = true;
    }

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

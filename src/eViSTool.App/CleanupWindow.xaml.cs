using System.Windows;
using eViSTool.App.ViewModels;
using eViSTool.Core.Profiles;

namespace eViSTool.App;

/// <summary>«Уборка диска» профиля — из настроек профиля. Модель — <see cref="CleanupViewModel"/>.</summary>
public partial class CleanupWindow : Window
{
    public CleanupWindow(GameProfile profile)
    {
        InitializeComponent();
        var vm = new CleanupViewModel(profile);
        DataContext = vm;
        Loaded += async (_, _) => await vm.ScanAsync();
        // остановили сервер или закрыли игру — кнопка оживает сама, окно закрывать не нужно
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => vm.CheckRunning();
        timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    public static void Open(Window owner, GameProfile profile) =>
        new CleanupWindow(profile) { Owner = owner }.ShowDialog();
}

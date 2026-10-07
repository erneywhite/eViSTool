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
    }

    public static void Open(Window owner, GameProfile profile) =>
        new CleanupWindow(profile) { Owner = owner }.ShowDialog();
}

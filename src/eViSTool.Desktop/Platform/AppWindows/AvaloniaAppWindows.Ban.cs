using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    public BanChoice? Ban(string player) =>
        new BanWindow { DataContext = new BanViewModel(player) }.ShowModal<BanChoice>();
}

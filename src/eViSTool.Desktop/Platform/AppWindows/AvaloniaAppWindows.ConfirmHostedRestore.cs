using eViSTool.Core.Hosting;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>Вопрос перед восстановлением копии мира на сервер у хостера: ответ или null, если отказались.</summary>
    public HostedRestoreChoice? ConfirmHostedRestore(HostedWorldBackup backup, string serverName) =>
        new HostedRestoreWindow { DataContext = new HostedRestoreViewModel(backup, serverName) }.ShowModal<HostedRestoreChoice>();
}

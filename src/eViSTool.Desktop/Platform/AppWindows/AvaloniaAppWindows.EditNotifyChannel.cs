using eViSTool.Core.Notifications;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>Канал оповещений после «Сохранить»; null — закрыли без сохранения.</summary>
    public NotifyChannel? EditNotifyChannel(NotifyChannel channel, bool isNew) =>
        new NotifyChannelWindow { DataContext = new NotifyChannelEditorViewModel(channel, isNew) }.ShowModal<NotifyChannel>();
}

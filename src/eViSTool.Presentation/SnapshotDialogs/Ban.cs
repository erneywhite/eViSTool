using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Окно бана игрока: dialog.ban (короткий ник, срок «1 день») и dialog.ban.long (ник, при котором заголовок не
/// помещается в строку и переносится). Нажатий нет: окно только открывается.
/// </summary>
internal static class Ban
{
    [SnapshotDialog("ban")]
    private static void Open(MainViewModel main) => AppWindows.Current.Ban("Вася");

    [SnapshotDialog("ban.long")]
    private static void OpenLong(MainViewModel main) => AppWindows.Current.Ban("Очень_Длинный_Ник_Игрока_Который_Не_Помещается");
}

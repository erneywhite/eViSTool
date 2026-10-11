using eViSTool.Core.Game;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Окно «Сервер в избранном игры»: новый сервер (dialog.favoriteserver — как открывает «В избранное» / «Добавить сервер»)
/// и правка записи с паролем (dialog.favoriteserver.edit). Окно на снимке только открывается: в clientsettings.json игры
/// ничего не пишется. Адрес — из example.org, пароль выдуманный.
/// </summary>
internal static class FavoriteServer
{
    /// <summary>Новый сервер: поля пустые, фокус в названии.</summary>
    [SnapshotDialog("favoriteserver")]
    private static void New(MainViewModel main) => AppWindows.Current.EditFavoriteServer(null);

    /// <summary>Правка записи с паролем: поля заполнены, пароль точками, фокус в адресе.</summary>
    [SnapshotDialog("favoriteserver.edit")]
    private static void Edit(MainViewModel main) =>
        AppWindows.Current.EditFavoriteServer(new PlayTarget(
            "fav:Сервер друзей|play.example.org:42421", PlayTargetKind.Favorite, "Сервер друзей", "play.example.org:42421", "made-up-password"));
}

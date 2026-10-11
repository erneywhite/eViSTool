using eViSTool.Core.Game;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>Владелец — открытое модальное окно или активное (вопрос из немодального «Найти сервер» встаёт над ним).</summary>
    public FavoriteServerChoice? EditFavoriteServer(PlayTarget? existing) =>
        new FavoriteServerWindow { DataContext = new FavoriteServerViewModel(existing) }.ShowModal<FavoriteServerChoice>();
}

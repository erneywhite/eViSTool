using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>
    /// «Найти сервер»: немодально, список с сервера загружается сразу после открытия.
    /// </summary>
    public void ShowFindServer(FindServerViewModel model)
    {
        new FindServerWindow { DataContext = model }.ShowModeless();
        _ = model.Load();
    }
}

using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>
    /// «Найти сервер»: немодально (как Show в окне на WPF), список с сервера загружается сразу после открытия (в окне на
    /// WPF — по Loaded).
    /// </summary>
    public void ShowFindServer(FindServerViewModel model)
    {
        new FindServerWindow { DataContext = model }.ShowModeless();
        _ = model.Load();
    }
}

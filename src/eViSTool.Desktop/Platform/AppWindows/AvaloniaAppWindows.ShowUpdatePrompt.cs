using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>«Вышла новая версия»: не модально — программой можно пользоваться, пока окно открыто.</summary>
    public void ShowUpdatePrompt(AboutViewModel about, Action skip) =>
        new UpdateWindow { DataContext = new UpdateWindowViewModel(about, skip) }.ShowModeless();
}

using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>«Установить также в»: ответ с отмеченными профилями и «запомнить»; null — отмена (Escape, крестик).</summary>
    public AlsoInstallAnswer? AskAlsoInstall(string hint, string currentName, string currentKind, string currentDetail, IReadOnlyList<AlsoChoice> others) =>
        new AlsoInstallWindow { DataContext = new AlsoInstallViewModel(hint, currentName, currentKind, currentDetail, others) }
            .ShowModal<AlsoInstallAnswer>();
}

using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>Выбор версии мода для отката: выбранный вариант или null (закрыли без выбора).</summary>
    public VersionOption? PickVersion(string heading, IReadOnlyList<VersionOption> options) =>
        new RollbackWindow(heading, options).ShowModal<VersionOption>();
}

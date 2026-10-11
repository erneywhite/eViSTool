using eViSTool.Core.Packs;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>Предпросмотр импорта модпака: режим и настройки модов или null, если импорт отменили.</summary>
    public PackImportChoice? ConfirmPackImport(PackImportPlan plan) =>
        new ImportPackWindow(new ImportPackViewModel(plan)).ShowModal<PackImportChoice>();
}

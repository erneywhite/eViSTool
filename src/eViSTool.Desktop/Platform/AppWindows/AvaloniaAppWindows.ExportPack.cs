using eViSTool.Core.Packs;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>Окно параметров модпака; null — закрыли без «Собрать». Путь к файлу спрашивает вызывающая модель.</summary>
    public PackExportOptions? ExportPack(string defaultName, int enabledCount, int disabledCount, bool hasModConfig) =>
        new ExportPackWindow(new ExportPackViewModel(defaultName, enabledCount, disabledCount, hasModConfig)).ShowModal<PackExportOptions>();
}

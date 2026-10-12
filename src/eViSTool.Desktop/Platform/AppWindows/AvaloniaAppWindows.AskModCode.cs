using eViSTool.Core.Packs;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>Код набора модов; null — закрыли без «Дальше».</summary>
    public ModCodeSet? AskModCode() =>
        new ModCodeWindow { DataContext = new ModCodeViewModel() }.ShowModal<ModCodeSet>();

    /// <summary>Готовый код набора — окно с кодом и копированием.</summary>
    public void ShowModCode(ModCodeShare share) =>
        new ModCodeShareWindow { DataContext = share }.ShowModal<object>();
}

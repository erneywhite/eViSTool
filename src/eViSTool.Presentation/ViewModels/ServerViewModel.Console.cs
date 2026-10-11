using System.Collections;
using CommunityToolkit.Mvvm.Input;

namespace eViSTool.Presentation.ViewModels;

public sealed partial class ServerViewModel
{
    /// <summary>
    /// «Копировать выделенные строки» в меню консоли: параметр — выделенные строки списка (SelectedItems). Ctrl и Shift
    /// выделяют вразнобой, поэтому в буфер строки идут в том порядке, в каком стоят в консоли. Строк нет — буфер не трогаем.
    /// </summary>
    [RelayCommand]
    private void CopyLines(IList? selected)
    {
        if (selected is not { Count: > 0 }) return;
        var chosen = selected.OfType<ConsoleLineViewModel>().ToHashSet(ReferenceEqualityComparer.Instance);
        var text = string.Join(Environment.NewLine, Lines.Where(chosen.Contains).Select(l => l.Text));
        if (text.Length > 0) Ui.Platform.SetClipboard(text);
    }
}

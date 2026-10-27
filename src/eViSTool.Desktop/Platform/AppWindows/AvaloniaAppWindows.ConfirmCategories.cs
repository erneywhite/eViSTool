using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>«Разложить по категориям»: принятые категории или null, если отменили.</summary>
    public CategoryChoices? ConfirmCategories(IReadOnlyList<CategoryProposal> proposals) =>
        new CategoryProposalsWindow(new CategoryProposalsViewModel(proposals)).ShowModal<CategoryChoices>();
}

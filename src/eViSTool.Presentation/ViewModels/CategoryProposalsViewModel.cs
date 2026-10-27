using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Предложенная категория: её название и моды, которые в неё лягут.</summary>
public sealed record CategoryProposal(string Category, IReadOnlyList<string> Mods);

/// <summary>Ответ окна «Разложить по категориям»: какие из предложенных категорий принять.</summary>
public sealed record CategoryChoices(IReadOnlyList<string> Accepted);

/// <summary>Строка окна: принять категорию или нет, сколько модов и каких.</summary>
public sealed partial class CategoryProposalRow(CategoryProposal proposal) : ObservableObject
{
    public string Category => proposal.Category;
    public string Title => Loc.T("catsuggest.row", proposal.Category, proposal.Mods.Count);
    public string ModsText => string.Join(", ", proposal.Mods);
    public int Count => proposal.Mods.Count;

    [ObservableProperty] private bool _isChecked = true;
}

/// <summary>
/// Окно «Разложить по категориям»: категории, подобранные по тегам ModDB, для модов, у которых категории ещё нет.
/// Снятая галочка — моды этой категории остаются без категории.
/// </summary>
public sealed partial class CategoryProposalsViewModel : ObservableObject
{
    public CategoryProposalsViewModel(IReadOnlyList<CategoryProposal> proposals)
    {
        Rows = [.. proposals.Select(p => new CategoryProposalRow(p))];
        foreach (var row in Rows) row.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Summary));
    }

    public IReadOnlyList<CategoryProposalRow> Rows { get; }

    public string Summary
    {
        get
        {
            var chosen = Rows.Where(r => r.IsChecked).ToList();
            return Loc.T("catsuggest.summary", chosen.Sum(r => r.Count), chosen.Count);
        }
    }

    public CategoryChoices Choice => new([.. Rows.Where(r => r.IsChecked).Select(r => r.Category)]);
}

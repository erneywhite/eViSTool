using Avalonia.Interactivity;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>«Разложить по категориям»: предложенные по тегам ModDB категории с галочками. Ответ — <see cref="CategoryChoices"/> или ничего (отмена).</summary>
public partial class CategoryProposalsWindow : DialogWindow
{
    public CategoryProposalsWindow() => InitializeComponent();

    public CategoryProposalsWindow(CategoryProposalsViewModel model) : this() => DataContext = model;

    private void Apply_Click(object? sender, RoutedEventArgs e) => Close((DataContext as CategoryProposalsViewModel)?.Choice);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}

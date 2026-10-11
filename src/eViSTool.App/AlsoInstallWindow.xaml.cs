using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Presentation.ViewModels;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;

namespace eViSTool.App;

/// <summary>Строка окна: профиль, что в него встанет, отмечен ли.</summary>
public sealed partial class AlsoInstallRow : ObservableObject
{
    public required string Name { get; init; }
    public required string KindText { get; init; }
    public required string Detail { get; init; }
    public bool IsEnabled { get; init; }
    public object? Tag { get; init; }

    [ObservableProperty] private bool _isChecked;
}

/// <summary>
/// «Установить также в»: мод ставится в текущий профиль и, по галочкам, в другие (сервер ↔ клиент) —
/// каждому своя версия под его игру (см. <see cref="AlsoInstall"/>).
/// </summary>
public partial class AlsoInstallWindow : Window
{
    private readonly List<AlsoInstallRow> _rows;

    /// <summary>Отмеченные профили, кроме текущего (после «Установить») — их Tag.</summary>
    public IReadOnlyList<object> Chosen { get; private set; } = [];

    /// <summary>Запомнить отмеченное как связь текущего профиля (в следующий раз отмечено заранее).</summary>
    public bool RememberChoice { get; private set; }

    /// <param name="others">Другие профили; Linked — связан с текущим (отмечен заранее, если туда можно поставить).</param>
    public AlsoInstallWindow(string hint, string currentName, string currentKind, string currentDetail,
        IEnumerable<AlsoChoice> others)
    {
        InitializeComponent();
        Hint.Text = hint;
        RememberText.Text = Loc.T("also.remember", currentName);
        _rows =
        [
            new AlsoInstallRow { Name = currentName, KindText = currentKind, Detail = currentDetail, IsEnabled = false, IsChecked = true },
            .. others.Select(o => new AlsoInstallRow
            {
                Name = o.Profile.Name, KindText = o.Profile.KindText, Detail = o.Detail, Tag = o.Tag, IsEnabled = o.CanInstall,
                IsChecked = o.Linked && o.CanInstall,
            }),
        ];
        Rows.ItemsSource = _rows;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Chosen = [.. _rows.Where(r => r.IsChecked && r.IsEnabled && r.Tag is not null).Select(r => r.Tag!)];
        RememberChoice = Remember.IsChecked == true;
        DialogResult = true;
    }
}

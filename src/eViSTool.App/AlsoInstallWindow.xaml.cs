using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
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
    public AlsoInstallOption? Option { get; init; }

    [ObservableProperty] private bool _isChecked;
}

/// <summary>
/// «Установить также в»: мод ставится в текущий профиль и, по галочкам, в другие (сервер ↔ клиент) —
/// каждому своя версия под его игру (см. <see cref="AlsoInstall"/>).
/// </summary>
public partial class AlsoInstallWindow : Window
{
    private readonly List<AlsoInstallRow> _rows;

    /// <summary>Отмеченные профили, кроме текущего (после «Установить»).</summary>
    public IReadOnlyList<AlsoInstallOption> Chosen { get; private set; } = [];

    public AlsoInstallWindow(string modName, string currentName, string currentKind, string currentDetail,
        IEnumerable<(AlsoInstallOption Option, string Name, string KindText)> others)
    {
        InitializeComponent();
        Hint.Text = Loc.T("also.hint", modName);
        _rows =
        [
            new AlsoInstallRow { Name = currentName, KindText = currentKind, Detail = currentDetail, IsEnabled = false, IsChecked = true },
            .. others.Select(o => new AlsoInstallRow
            {
                Name = o.Name, KindText = o.KindText, Detail = o.Option.Describe(), Option = o.Option, IsEnabled = o.Option.CanInstall,
            }),
        ];
        Rows.ItemsSource = _rows;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Chosen = [.. _rows.Where(r => r.IsChecked && r.Option is { CanInstall: true }).Select(r => r.Option!)];
        DialogResult = true;
    }
}

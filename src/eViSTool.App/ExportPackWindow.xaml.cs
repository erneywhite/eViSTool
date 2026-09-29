using System.Windows;
using eViSTool.Core.Localization;
using eViSTool.Core.Packs;

namespace eViSTool.App;

/// <summary>Параметры сборки модпака. Путь к файлу спрашивается отдельно, после этого окна.</summary>
public partial class ExportPackWindow : Window
{
    public PackExportOptions? Options { get; private set; }

    public ExportPackWindow(string defaultName, int enabledCount, int disabledCount, bool hasModConfig)
    {
        InitializeComponent();
        NameBox.Text = defaultName;
        ConfigBox.IsEnabled = hasModConfig;
        Summary.Text = Loc.T("packx.summary", enabledCount, disabledCount);
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void Build_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            NameBox.Focus();
            return;
        }
        Options = new PackExportOptions
        {
            Name = name,
            Description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim(),
            BundleFiles = BundleBox.IsChecked == true,
            IncludeModConfig = ConfigBox.IsChecked == true,
            IncludeDisabled = DisabledBox.IsChecked == true,
        };
        DialogResult = true;
    }
}

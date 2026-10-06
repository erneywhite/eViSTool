using System.Windows;
using eViSTool.Core.Localization;
using eViSTool.Core.Packs;

namespace eViSTool.App;

/// <summary>Строка предпросмотра импорта.</summary>
public sealed record PackItemRow(string Name, string PackVersion, string MineVersion, string ActionText, string SourceText);

/// <summary>Предпросмотр импорта модпака: что поставится/обновится/откатится, режим и настройки модов.</summary>
public partial class ImportPackWindow : Window
{
    public bool Mirror => MirrorMode.IsChecked == true;
    public bool ApplyConfig => ConfigBox.IsChecked == true;

    public ImportPackWindow(PackImportPlan plan)
    {
        InitializeComponent();
        var m = plan.Manifest;

        PackName.Text = m.Name;
        PackInfo.Text = Loc.T("packi.info", m.Mods.Count, m.GameVersion ?? "?", m.Created.ToLocalTime().ToString("d"), m.CreatedWith ?? "?");
        PackDescription.Text = m.Description ?? "";
        PackDescription.Visibility = string.IsNullOrWhiteSpace(m.Description) ? Visibility.Collapsed : Visibility.Visible;

        if (plan.GameVersionWarning is { } w)
        {
            WarningText.Text = w;
            WarningBox.Visibility = Visibility.Visible;
        }

        Items.ItemsSource = plan.Items
            .OrderBy(i => i.Action is PackItemAction.Same or PackItemAction.Duplicate) // сначала то, что изменится
            .ThenBy(i => i.Mod.Name, StringComparer.OrdinalIgnoreCase)
            .Select(i => new PackItemRow(
                i.Mod.Name + (i.Mod.Enabled ? "" : " " + Loc.T("packi.disabledMark")),
                i.Mod.Version ?? "?",
                i.Installed?.Info?.Version ?? "—",
                Loc.T(i.Action switch
                {
                    PackItemAction.Install => "packi.actInstall",
                    PackItemAction.Update => "packi.actUpdate",
                    PackItemAction.Downgrade => "packi.actDowngrade",
                    PackItemAction.Duplicate => "packi.actDuplicate",
                    _ => "packi.actSame",
                }),
                i.Action is PackItemAction.Same or PackItemAction.Duplicate ? "" : Loc.T(i.Mod.Bundled ? "packi.srcInside" : "packi.srcModDb")))
            .ToList();

        MirrorMode.Content = Loc.T("packi.modeMirror", plan.NotInPack.Count);
        MirrorMode.IsEnabled = plan.NotInPack.Count > 0;
        MirrorMode.ToolTip = plan.NotInPack.Count > 0
            ? string.Join("\n", plan.NotInPack.Select(l => l.Info?.Name ?? l.FileName))
            : null;

        if (m.IncludesModConfig)
        {
            ConfigBox.Visibility = Visibility.Visible;
            ConfigBox.IsChecked = true;
        }

        var changes = plan.Items.Count(i => i.Action is not (PackItemAction.Same or PackItemAction.Duplicate));
        var downloads = plan.Items.Count(i => i.Action is not (PackItemAction.Same or PackItemAction.Duplicate) && !i.Mod.Bundled);
        Summary.Text = Loc.T("packi.summary", changes, downloads);
    }

    private void Import_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Localization;

namespace eViSTool.App;

/// <summary>Вариант для отката: сохранённая копия (Path) или релиз из модбазы (Release).</summary>
public sealed record VersionOption(string Version, string Source, string Date, string Note, string? Path, ModDbRelease? Release);

/// <summary>Окно выбора версии мода: сохранённые копии + релизы модбазы для ветки игры.</summary>
public partial class RollbackWindow : Window, INotifyPropertyChanged
{
    public ObservableCollection<VersionOption> Options { get; } = [];
    public string Heading { get; }

    private string _status = "";
    public string Status { get => _status; set { _status = value; OnChanged(); } }

    public VersionOption? Selected { get; private set; }

    public RollbackWindow(string title, IEnumerable<VersionOption> options)
    {
        InitializeComponent();
        Heading = title;
        foreach (var o in options) Options.Add(o);
        Status = Options.Count == 0 ? Loc.T("rollback.none") : "";
        DataContext = this;
    }

    /// <summary>Собирает варианты: сначала сохранённые копии, потом модбаза.</summary>
    public static List<VersionOption> BuildOptions(ModBackupStore? store, string modId, string? installedVersion,
        IReadOnlyList<ModDbRelease> releases)
    {
        var list = new List<VersionOption>();
        foreach (var path in store?.List(modId) ?? [])
        {
            var info = File.Exists(path) ? ModScanner.ReadZip(path).Info : null;
            var version = info?.Version ?? System.IO.Path.GetFileName(path);
            list.Add(new VersionOption(version, Loc.T("rollback.savedCopy"), File.GetCreationTime(path).ToString("g"),
                version == installedVersion ? Loc.T("rollback.installed") : "", path, null));
        }
        foreach (var r in releases)
        {
            var date = DateTime.TryParse(r.Created, out var d) ? d.ToString("d") : r.Created ?? "";
            list.Add(new VersionOption(r.ModVersion ?? "?", "ModDB", date,
                r.ModVersion == installedVersion ? Loc.T("rollback.installed") : "", null, r));
        }
        return list;
    }

    private void Install_Click(object sender, RoutedEventArgs e) => Accept();
    private void List_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Accept();

    private void Accept()
    {
        if (List.SelectedItem is not VersionOption o)
        {
            Status = Loc.T("rollback.pick");
            return;
        }
        Selected = o;
        DialogResult = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

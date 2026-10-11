using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using eViSTool.Presentation.ViewModels;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Localization;

namespace eViSTool.App;

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

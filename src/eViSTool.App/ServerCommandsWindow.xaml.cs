using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using eViSTool.App.ViewModels;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;

namespace eViSTool.App;

/// <summary>
/// Все команды сервера с поиском — «интерактивный /help». Список — из ответа самого сервера (с командами модов);
/// выбранная команда вставляется в поле ввода консоли. Список обновляется на лету, когда сервер ответит на /help.
/// </summary>
public partial class ServerCommandsWindow : Window, INotifyPropertyChanged
{
    private readonly ServerViewModel _server;
    private string _search = "";

    public ServerCommand? Chosen { get; private set; }

    public ServerCommandsWindow(ServerViewModel server)
    {
        InitializeComponent();
        _server = server;
        _server.PropertyChanged += Server_PropertyChanged;
        Closed += (_, _) => _server.PropertyChanged -= Server_PropertyChanged;
        DataContext = this;
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void Server_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServerViewModel.Commands)) Refilter();
        if (e.PropertyName is nameof(ServerViewModel.CanCommand)) OnChanged(nameof(CanRefresh));
    }

    public string Search
    {
        get => _search;
        set { _search = value; OnChanged(); Refilter(); }
    }

    /// <summary>Подходящие под поиск: по имени, аргументам и описанию, все слова сразу.</summary>
    public IReadOnlyList<ServerCommand> Shown
    {
        get
        {
            var words = Search.Trim().TrimStart('/').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return _server.Commands
                .Where(c => words.All(w => c.Usage.Contains(w, StringComparison.OrdinalIgnoreCase)
                                           || c.Description.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public string Status => _server.Commands.Count == 0
        ? Loc.T("cmds.empty")
        : Loc.T("cmds.count", Shown.Count, _server.Commands.Count);

    /// <summary>Попросить у сервера свежий список (/help) — только у работающего.</summary>
    public bool CanRefresh => _server.CanCommand;

    private void Refilter()
    {
        OnChanged(nameof(Shown));
        OnChanged(nameof(Status));
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _server.RequestCommandList();

    private void Insert_Click(object sender, RoutedEventArgs e) => Choose(List.SelectedItem as ServerCommand ?? Shown.FirstOrDefault());

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Choose(List.SelectedItem as ServerCommand);

    // из поиска: ↓ — в список, Enter — первая подходящая
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && List.Items.Count > 0)
        {
            List.SelectedIndex = 0;
            (List.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus();
            e.Handled = true;
        }
    }

    private void Choose(ServerCommand? command)
    {
        if (command is null) return;
        Chosen = command;
        DialogResult = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

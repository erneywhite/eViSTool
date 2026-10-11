using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Окно «Команды сервера» — интерактивный /help: все команды сервера (с командами модов) с поиском. Список берётся у
/// <see cref="ServerViewModel.Commands"/> и обновляется на лету, когда сервер ответит на /help; выбранная команда уходит
/// окну через <see cref="Accepted"/> и встаёт в поле ввода консоли. Модель подписана на сервер, пока окно открыто:
/// после закрытия её надо освободить (<see cref="Dispose"/>), иначе она так и будет слушать сервер.
/// </summary>
public sealed partial class ServerCommandsViewModel : ObservableObject, IDisposable
{
    private readonly ServerViewModel _server;
    private readonly Action _requestList;
    private readonly ObservableCollection<ServerCommand> _all = [];
    private string[] _words = [];
    private bool _disposed;

    public ServerCommandsViewModel(ServerViewModel server) : this(server, server.RequestCommandList)
    {
    }

    /// <param name="requestList">Как попросить у сервера свежий список (/help); тесту — своя, без настоящего сервера.</param>
    public ServerCommandsViewModel(ServerViewModel server, Action requestList)
    {
        _server = server;
        _requestList = requestList;
        View = new FilteredList<ServerCommand>(_all, Matches, ByName);
        SyncCommands();
        _server.PropertyChanged += OnServerChanged;
    }

    /// <summary>
    /// Подходящие под поиск, по имени. Вид, а не новый список на каждый символ: те же объекты остаются на месте, поэтому
    /// выбранная строка и прокрутка не сбрасываются, когда сервер присылает обновлённый список.
    /// </summary>
    public FilteredList<ServerCommand> View { get; }

    /// <summary>Что ищем: слова через пробел, все сразу — в имени с аргументами и в описании; ведущий «/» не мешает.</summary>
    [ObservableProperty] private string _search = "";

    /// <summary>Выбранная строка списка (null — ничего не выбрано).</summary>
    [ObservableProperty] private ServerCommand? _selected;

    /// <summary>Строка под списком: «Список пока неизвестен…» или «найдено из всех».</summary>
    public string Status => _all.Count == 0 ? Loc.T("cmds.empty") : Loc.T("cmds.count", View.Count, _all.Count);

    /// <summary>Попросить у сервера свежий список (/help) можно только у работающего.</summary>
    public bool CanRefresh => _server.CanCommand;

    /// <summary>Команда выбрана («Вставить», двойной щелчок, Enter): окно закрывается с этим ответом.</summary>
    public event Action<ServerCommand>? Accepted;

    partial void OnSearchChanged(string value)
    {
        _words = (value ?? "").Trim().TrimStart('/').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        View.Refresh();
        DropSelectionIfHidden();
        OnPropertyChanged(nameof(Status));
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void Refresh() => _requestList();

    /// <summary>
    /// Вставить команду: заданную (двойной щелчок передаёт строку), иначе выбранную, а если ничего не выбрано — первую из
    /// найденных, чтобы набрал, нажал Enter — и готово. Список пуст — окно остаётся.
    /// </summary>
    [RelayCommand]
    private void Insert(ServerCommand? command)
    {
        var chosen = command ?? Selected ?? View.Items.FirstOrDefault();
        if (chosen is not null) Accepted?.Invoke(chosen);
    }

    private bool Matches(ServerCommand command) =>
        _words.All(w => command.Usage.Contains(w, StringComparison.OrdinalIgnoreCase)
                        || command.Description.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static int ByName(ServerCommand a, ServerCommand b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    private void OnServerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServerViewModel.Commands))
        {
            SyncCommands();
            DropSelectionIfHidden();
            OnPropertyChanged(nameof(Status));
        }
        else if (e.PropertyName is nameof(ServerViewModel.CanCommand))
        {
            OnPropertyChanged(nameof(CanRefresh));
            RefreshCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Привести свой список к списку сервера, не пересоздавая то, что не изменилось (команды сравниваются по значению):
    /// иначе выбранная строка терялась бы при каждом ответе на /help. Одинаковые команды сервера сливаются в одну.
    /// </summary>
    private void SyncCommands()
    {
        var incoming = new HashSet<ServerCommand>(_server.Commands);
        using (View.DeferRefresh())
        {
            for (var i = _all.Count - 1; i >= 0; i--)
                if (!incoming.Contains(_all[i])) _all.RemoveAt(i);
            var known = new HashSet<ServerCommand>(_all);
            foreach (var command in _server.Commands)
                if (known.Add(command)) _all.Add(command);
        }
    }

    /// <summary>Выбранная строка пропала из вида (поиск или новый список) — выбор снимается.</summary>
    private void DropSelectionIfHidden()
    {
        if (Selected is { } selected && !View.Items.Contains(selected)) Selected = null;
    }

    /// <summary>Закрыли окно: больше не слушать сервер.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _server.PropertyChanged -= OnServerChanged;
    }
}

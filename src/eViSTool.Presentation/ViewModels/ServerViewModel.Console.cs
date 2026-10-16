using System.Collections;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Server;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Консоль раздела «Сервер» глазами вида: строки, поле команды, подсказки и история живут в
/// <see cref="ServerConsoleViewModel"/>, а вид и окно «Команды» привязаны к разделу — здесь те же свойства под теми же
/// именами, уведомления о них раздел передаёт от консоли (подписка в конструкторе). Команды кнопок остаются у раздела:
/// им нужны его «занят» и строка ошибки.
/// </summary>
public sealed partial class ServerViewModel
{
    public ObservableCollection<ConsoleLineViewModel> Lines => _console.Lines;

    /// <summary>Автопрокрутка: пока пользователь внизу — едем за новыми строками. true — прокрутить в любом случае (первая порция).</summary>
    public event Action<bool>? LinesAppended
    {
        add => _console.LinesAppended += value;
        remove => _console.LinesAppended -= value;
    }

    public string CommandText
    {
        get => _console.CommandText;
        set => _console.CommandText = value;
    }

    /// <summary>Команды сервера, которые знает агент.</summary>
    public IReadOnlyList<ServerCommand> Commands => _console.Commands;

    public IReadOnlyList<ServerCommand> Suggestions
    {
        get => _console.Suggestions;
        set => _console.Suggestions = value;
    }

    public bool ShowSuggestions
    {
        get => _console.ShowSuggestions;
        set => _console.ShowSuggestions = value;
    }

    public ServerCommand? SelectedSuggestion
    {
        get => _console.SelectedSuggestion;
        set => _console.SelectedSuggestion = value;
    }

    /// <summary>Под полем: «/tp &lt;source&gt; &lt;target&gt; — Teleport…» для набираемой команды.</summary>
    public string ArgsHint
    {
        get => _console.ArgsHint;
        set => _console.ArgsHint = value;
    }

    /// <summary>Tab: выбранная подсказка или общее начало подходящих команд. false — дописывать нечего.</summary>
    public bool CompleteCommand() => _console.CompleteCommand();

    /// <summary>↑/↓ при открытых подсказках — по списку подсказок (иначе — история).</summary>
    public void MoveSuggestion(int direction) => _console.MoveSuggestion(direction);

    public void HideSuggestions() => _console.HideSuggestions();

    /// <summary>Команда из окна «Команды» или клик по подсказке — в поле ввода, дальше набирать аргументы.</summary>
    public void InsertCommand(ServerCommand command) => _console.InsertCommand(command);

    /// <summary>↑/↓ в поле команды — история, как в консоли.</summary>
    public void HistoryStep(int direction) => _console.HistoryStep(direction);

    /// <summary>
    /// Задать список команд сервера (обычно его присылает агент в ответ на /help): подсказки в консоли и окно «Команды»
    /// обновляются. Нужен тестам и снимкам окна, где агента нет.
    /// </summary>
    public void SetCommands(IReadOnlyList<ServerCommand> list) => _console.SetCommands(list);

    /// <summary>Попросить у сервера список команд: его ответ на /help агент разберёт и запомнит.</summary>
    public void RequestCommandList()
    {
        if (Client is { } client && CanCommand) _ = client.CommandAsync("/help");
    }

    /// <summary>Кнопка «Команды»: все команды сервера с поиском; выбранная — в поле ввода.</summary>
    [RelayCommand]
    private void ShowCommands()
    {
        if (Commands.Count == 0) RequestCommandList(); // список ещё не знаем — сразу спросим у сервера
        if (AppWindows.Current.PickServerCommand(this) is { } chosen)
        {
            InsertCommand(chosen);
            CommandInserted?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Команда вставлена из окна — вид переводит фокус в поле ввода.</summary>
    public event EventHandler? CommandInserted;

    [RelayCommand]
    private Task SendCommand() => Do(_console.SendAsync);

    [RelayCommand]
    private void ClearConsole() => Lines.Clear();

    /// <summary>
    /// «Копировать выделенные строки» в меню консоли: параметр — выделенные строки списка (SelectedItems). Ctrl и Shift
    /// выделяют вразнобой, поэтому в буфер строки идут в том порядке, в каком стоят в консоли. Строк нет — буфер не трогаем.
    /// </summary>
    [RelayCommand]
    private void CopyLines(IList? selected)
    {
        if (selected is not { Count: > 0 }) return;
        var chosen = selected.OfType<ConsoleLineViewModel>().ToHashSet(ReferenceEqualityComparer.Instance);
        var text = string.Join(Environment.NewLine, Lines.Where(chosen.Contains).Select(l => l.Text));
        if (text.Length > 0) Ui.Platform.SetClipboard(text);
    }
}

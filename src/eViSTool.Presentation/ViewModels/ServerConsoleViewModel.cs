using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Консоль раздела «Сервер»: строки сервера (долгий опрос агента), поле команды с подсказками и историей, список команд
/// сервера. Связь с агентом — у наблюдателя (<see cref="ServerMonitor"/>), консоль берёт текущую через
/// <paramref name="client"/>: связь сменилась — поток строк прежней сам останавливается. Вид привязан к разделу
/// (<see cref="ServerViewModel"/>), а раздел отдаёт ему свойства и уведомления консоли под теми же именами.
/// </summary>
/// <param name="client">Текущая связь с агентом (null — агента нет или связи нет).</param>
internal sealed partial class ServerConsoleViewModel(Func<AgentClient?> client) : ObservableObject
{
    private const int MaxLines = 5000;

    private long _lastSeq;
    private int _agentPid;
    private readonly List<string> _history = [];
    private int _historyPos = -1;

    public ObservableCollection<ConsoleLineViewModel> Lines { get; } = [];

    /// <summary>Автопрокрутка: пока пользователь внизу — едем за новыми строками. true — прокрутить в любом случае (первая порция).</summary>
    public event Action<bool>? LinesAppended;

    /// <summary>Пришли новые строки (уже в <see cref="Lines"/>): вкладке «Игроки» нужен ответ сервера на её команду.</summary>
    public event Action<IReadOnlyList<ConsoleLine>>? LinesReceived;

    [ObservableProperty] private string _commandText = "";

    // ---- подсказки команд: список — из ответа сервера на /help (агент его запоминает), Tab дописывает

    /// <summary>Команды сервера, которые знает агент.</summary>
    public IReadOnlyList<ServerCommand> Commands { get; private set; } = [];
    private int _commandCount = -1;

    [ObservableProperty] private IReadOnlyList<ServerCommand> _suggestions = [];
    [ObservableProperty] private bool _showSuggestions;
    [ObservableProperty] private ServerCommand? _selectedSuggestion;

    /// <summary>Под полем: «/tp &lt;source&gt; &lt;target&gt; — Teleport…» для набираемой команды.</summary>
    [ObservableProperty] private string _argsHint = "";
    private bool _quietText; // текст пришёл из истории (↑/↓) — подсказки не всплывают

    partial void OnCommandTextChanged(string value)
    {
        if (_quietText) return;
        UpdateSuggestions(value);
    }

    private void UpdateSuggestions(string value)
    {
        Suggestions = ServerCommands.Suggest(Commands, value);
        // подсказывать нечего, кроме уже набранного целиком — список не нужен, хватит подсказки аргументов
        ShowSuggestions = Suggestions.Count > 0
                          && !(Suggestions.Count == 1 && string.Equals("/" + Suggestions[0].Name, value, StringComparison.OrdinalIgnoreCase));
        SelectedSuggestion = null;
        ArgsHint = ServerCommands.Typed(Commands, value) is { } typed && !ShowSuggestions
            ? $"{typed.Usage}   —   {typed.Description}"
            : value.StartsWith('/') && Commands.Count == 0 ? Loc.T("cmds.unknownHint") : "";
    }

    /// <summary>Tab: выбранная подсказка или общее начало подходящих команд. false — дописывать нечего.</summary>
    public bool CompleteCommand()
    {
        var completed = SelectedSuggestion is { } chosen ? "/" + chosen.Name + " " : ServerCommands.Complete(Suggestions, CommandText);
        if (completed is null || completed == CommandText) return false;
        CommandText = completed;
        return true;
    }

    /// <summary>↑/↓ при открытых подсказках — по списку подсказок (иначе — история).</summary>
    public void MoveSuggestion(int direction)
    {
        if (Suggestions.Count == 0) return;
        var i = SelectedSuggestion is null ? (direction > 0 ? -1 : Suggestions.Count) : Suggestions.ToList().IndexOf(SelectedSuggestion);
        SelectedSuggestion = Suggestions[Math.Clamp(i + direction, 0, Suggestions.Count - 1)];
    }

    public void HideSuggestions() => ShowSuggestions = false;

    /// <summary>Команда из окна «Команды» или клик по подсказке — в поле ввода, дальше набирать аргументы.</summary>
    public void InsertCommand(ServerCommand command) => CommandText = "/" + command.Name + " ";

    /// <summary>
    /// Задать список команд сервера (обычно его присылает агент в ответ на /help, см. <see cref="LoadCommandsAsync"/>):
    /// подсказки в консоли и окно «Команды» обновляются. Нужен тестам и снимкам окна, где агента нет.
    /// </summary>
    public void SetCommands(IReadOnlyList<ServerCommand> list)
    {
        Commands = list;
        OnPropertyChanged(nameof(Commands));
        if (!_quietText) UpdateSuggestions(CommandText);
    }

    /// <summary>Статус агента: число команд у него сменилось (сервер ответил на /help) — перечитать список.</summary>
    public void CheckCommands(AgentStatus s)
    {
        if (s.CommandCount != _commandCount && client() is { } commandsFrom)
        {
            _commandCount = s.CommandCount;
            _ = LoadCommandsAsync(commandsFrom);
        }
    }

    private async Task LoadCommandsAsync(AgentClient agent)
    {
        try
        {
            var list = await agent.CommandsAsync();
            if (agent != client()) return; // пока ждали, связь сменилась
            Commands = list;
            OnPropertyChanged(nameof(Commands));
            if (!_quietText) UpdateSuggestions(CommandText);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            _commandCount = -1; // старый агент без списка команд или связь оборвалась — попробуем со следующим статусом
        }
    }

    /// <summary>Статус агента: агент перезапускался — нумерация строк началась заново, прежние строки убираем.</summary>
    public void CheckSequence(AgentStatus s)
    {
        if (s.AgentPid != _agentPid || s.LastSeq < _lastSeq)
        {
            _agentPid = s.AgentPid;
            if (s.LastSeq < _lastSeq)
            {
                Lines.Clear();
                _lastSeq = 0;
            }
        }
    }

    /// <summary>Сменился профиль: строки, нумерация и список команд — с чистого листа (история команд остаётся).</summary>
    public void Reset()
    {
        Lines.Clear();
        _lastSeq = 0;
        _agentPid = 0;
        Commands = [];
        _commandCount = -1;
        OnPropertyChanged(nameof(Commands));
        UpdateSuggestions(CommandText);
    }

    /// <summary>Поток консоли: долгий опрос агента — строка появилась, сразу пришла.</summary>
    public async Task PumpAsync(AgentClient agent, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ReferenceEquals(agent, client()))
        {
            try
            {
                var lines = await agent.ConsoleAsync(_lastSeq, 25, ct);
                if (lines.Count == 0) continue;
                var first = Lines.Count == 0; // подхватили агента — сразу к последним строкам
                foreach (var l in lines) Lines.Add(new ConsoleLineViewModel(l));
                _lastSeq = lines[^1].Seq;
                LinesReceived?.Invoke(lines); // ответ сервера на команду вкладки «Игроки»
                while (Lines.Count > MaxLines) Lines.RemoveAt(0);
                LinesAppended?.Invoke(first);
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                // связь мигнула — обычное дело; что-то другое (битый ответ) — в журнал, но поток консоли не бросаем
                if (ex is not (HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)) Ui.Platform.WriteCrashLog(ex);
                await Task.Delay(1000, CancellationToken.None);
                if (!ReferenceEquals(agent, client())) return;
            }
        }
    }

    /// <summary>Отправить набранную команду: «time» → «/time», как в игре; она уходит в историю, поле пустеет.</summary>
    public async Task SendAsync()
    {
        var text = CommandText.Trim();
        if (text.Length == 0 || client() is not { } agent) return;
        if (!text.StartsWith('/')) text = "/" + text; // «time» → «/time», как в игре
        _history.Remove(text);
        _history.Add(text);
        _historyPos = -1;
        CommandText = "";
        ShowSuggestions = false;
        await agent.CommandAsync(text);
    }

    /// <summary>↑/↓ в поле команды — история, как в консоли.</summary>
    public void HistoryStep(int direction)
    {
        if (_history.Count == 0) return;
        _historyPos = _historyPos < 0
            ? (direction < 0 ? _history.Count - 1 : -1)
            : _historyPos + direction;
        if (_historyPos >= _history.Count) _historyPos = -1;
        if (_historyPos < -1) _historyPos = 0;
        _quietText = true;
        CommandText = _historyPos < 0 ? "" : _history[Math.Max(0, _historyPos)];
        _quietText = false;
        ShowSuggestions = false;
        ArgsHint = ServerCommands.Typed(Commands, CommandText) is { } typed ? $"{typed.Usage}   —   {typed.Description}" : "";
    }
}

/// <summary>Цвет строки консоли по смыслу; сам цвет выбирает вид.</summary>
public enum ConsoleTint { Error, Warning, Input, System, Chat }

/// <summary>Строка консоли с цветом по уровню.</summary>
public sealed class ConsoleLineViewModel(ConsoleLine line)
{
    // ответы сервера бывают с разметкой игры (/help) — показываем обычным текстом
    public string Text { get; } = line.Kind == ConsoleLineKind.Input ? "> " + line.Text : ConsoleMarkup.ToPlain(line.Text);

    /// <summary>null — обычный цвет текста темы.</summary>
    public ConsoleTint? Tint { get; } = line.Kind switch
    {
        ConsoleLineKind.Error => ConsoleTint.Error,
        ConsoleLineKind.Input => ConsoleTint.Input,
        ConsoleLineKind.System => ConsoleTint.System,
        _ => (line.Level?.Replace("Server ", "", StringComparison.Ordinal)) switch // в консоли уровни вида «Server Event»
        {
            "Error" or "Fatal" => ConsoleTint.Error,
            "Warning" => ConsoleTint.Warning,
            "Chat" => ConsoleTint.Chat,
            _ => null,
        },
    };

    public double Opacity { get; } = line.Kind == ConsoleLineKind.Output
                                     && line.Level?.EndsWith("Notification", StringComparison.Ordinal) == true ? 0.75 : 1;
}

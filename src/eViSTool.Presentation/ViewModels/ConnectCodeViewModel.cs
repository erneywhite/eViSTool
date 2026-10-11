using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Окно «Сервер на другом компьютере»: вставить код подключения, увидеть, куда он ведёт (адрес и порт — чтобы не
/// перепутать серверы), и проверить связь с агентом. Сам код — секрет (в нём ключ): поле в окне скрытое, а модель его
/// никуда не пишет и не показывает. Ответ окна — разобранный <see cref="Code"/>.
/// </summary>
public sealed partial class ConnectCodeViewModel : ObservableObject
{
    /// <summary>
    /// Текст для поля при открытии — только для снимка окна (dialog.connectcode.bad): у снимка нет другого способа
    /// показать окно с введённым кодом. Берётся один раз, первой созданной моделью.
    /// </summary>
    internal static string? SnapshotText;

    private readonly Func<ConnectionCode, CancellationToken, Task<AgentStatus>> _status;
    // проверка, которая идёт сейчас: код поменяли — её отменяют, и её ответ окно уже не покажет (в окне на WPF то же
    // делал счётчик проверок)
    private CancellationTokenSource? _check;

    /// <param name="okText">Надпись на кнопке «ОК»: «Подключить», «Сохранить».</param>
    /// <param name="status">Запрос состояния агента по коду; по умолчанию — настоящий агент (тесты подменяют).</param>
    public ConnectCodeViewModel(string okText, Func<ConnectionCode, CancellationToken, Task<AgentStatus>>? status = null)
    {
        OkText = okText;
        _status = status ?? RemoteStatusAsync;
        if (Interlocked.Exchange(ref SnapshotText, null) is { } text) Text = text;
    }

    public string OkText { get; }

    /// <summary>Код подключения, как его вставили (секрет: в окне — поле с точками).</summary>
    [ObservableProperty] private string _text = "";

    /// <summary>Разобранный код; null — поле пустое или в нём не код.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    private ConnectionCode? _code;

    /// <summary>Куда ведёт код («Подключение к …, порт …») или что это не код; пусто — поле пустое.</summary>
    [ObservableProperty] private string _parsedText = "";
    [ObservableProperty] private RowTone _parsedTone = RowTone.Muted;

    /// <summary>Итог проверки связи: «Подключаюсь…», «Подключено…» или ошибка подключения.</summary>
    [ObservableProperty] private string _checkText = "";
    [ObservableProperty] private RowTone _checkTone = RowTone.Muted;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    private bool _isChecking;

    /// <summary>«ОК» доступна, когда в поле разбираемый код.</summary>
    public bool CanAccept => Code is not null;

    partial void OnTextChanged(string value)
    {
        CancelCheck();
        CheckText = "";
        var text = value.Trim();
        Code = ConnectionCode.TryParse(text, out var code) ? code : null;
        ParsedText = text.Length == 0 ? "" : Code is { } c ? Loc.T("connect.target", c.Host, c.Port) : Loc.T("connect.bad");
        ParsedTone = Code is null && text.Length > 0 ? RowTone.Danger : RowTone.Muted;
    }

    private bool CanCheck => Code is not null && !IsChecking;

    /// <summary>
    /// Спросить агента о состоянии сервера. Пока ждём, код могут поменять: тогда проверку отменяют и её ответ не
    /// показывают — он уже не про тот код, что в поле. Поэтому команда допускает новый запуск, не дожидаясь старого.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheck), AllowConcurrentExecutions = true)]
    private async Task CheckAsync()
    {
        if (Code is not { } code) return;
        CancelCheck();
        _check = new CancellationTokenSource();
        var ct = _check.Token;
        IsChecking = true;
        CheckTone = RowTone.Muted;
        CheckText = Loc.T("connect.checking");
        string result;
        var ok = false;
        try
        {
            var status = await _status(code, ct);
            result = Loc.T("connect.ok", Loc.T(status.State switch
            {
                ServerState.Running => "server.stateRunning",
                ServerState.Starting => "server.stateStarting",
                ServerState.Stopping => "server.stateStopping",
                _ => "server.stateStopped",
            }), status.GameVersion ?? "?");
            ok = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // код поменяли или окно закрыли
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or UriFormatException)
        {
            result = RemoteSecret.Describe(ex);
        }
        if (ct.IsCancellationRequested) return; // ответ пришёл, но код уже другой
        _check = null;
        CheckText = result;
        CheckTone = ok ? RowTone.Good : RowTone.Danger;
        IsChecking = false;
    }

    /// <summary>Бросить идущую проверку (код поменяли, окно закрыли): её ответ уже не нужен, «Подключаюсь…» убирается.</summary>
    public void CancelCheck()
    {
        if (_check is not { } check) return;
        _check = null;
        check.Cancel(); // без Dispose: таймера у источника нет, а токен ещё может держать запрос, который сейчас отменяется
        IsChecking = false;
        CheckText = "";
    }

    private static async Task<AgentStatus> RemoteStatusAsync(ConnectionCode code, CancellationToken ct)
    {
        using var client = AgentClient.ForRemote(code);
        return await client.StatusAsync(ct).ConfigureAwait(false);
    }
}

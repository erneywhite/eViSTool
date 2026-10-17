using System.Globalization;
using eViSTool.Core.Diagnostics;
using eViSTool.Core.Server;

namespace eViSTool.Core.Hosting;

/// <summary>
/// Что делает сервер у хостера — по его логу (<see cref="HostedLogs"/>). Надёжны только <see cref="Stopped"/> и
/// <see cref="Crashed"/>: сервер, убитый без остановки, «застывает» в <see cref="Running"/>, поэтому «запущен» лишь
/// смягчает решения и не отменяет вопрос «останови сервер в панели хостера» (<see cref="HostedLogs.IsSurelyDown"/>).
/// </summary>
public enum HostedServerState
{
    /// <summary>Логов нет или прочитать их не вышло.</summary>
    Unknown,

    /// <summary>Лог начат, а «… now running on Port …» ещё нет.</summary>
    Starting,

    /// <summary>Сервер готов принимать игроков.</summary>
    Running,

    /// <summary>Остановка начата (SIGTERM из панели, /stop), а «Stopped the server!» ещё нет. Для вопроса — как Running.</summary>
    Stopping,

    /// <summary>«Stopped the server!» — или остановка начата и лог давно не растёт.</summary>
    Stopped,

    /// <summary>Вылет: свежий server-crash.log или строки вылета в логе.</summary>
    Crashed,
}

/// <summary>
/// Признаки одного запуска сервера по строкам его server-main.log: когда начат, готов ли, начата и закончена ли
/// остановка, был ли вылет. Строки приходят по порядку и по частям, сами они здесь не хранятся. Признаки — те же, что
/// у своего сервера: готовность и остановка — как у <see cref="ServerHost"/>, вылет — как у <see cref="CrashAnalyzer"/>
/// (плюс запись уровня Fatal), но остановка и вылет узнаются строже (см. <see cref="Add"/>): по ним не задаётся вопрос
/// «останови сервер». Время в строках — местное время сервера (у G-Portal UTC+2): не время этой машины и не UTC файлов
/// из листинга.
/// </summary>
/// <param name="fromMiddle">Чтение начато с середины файла (лог огромный): начала запуска не видно.</param>
public sealed class HostedRunMarks(bool fromMiddle = false)
{
    /// <summary>Последняя строка штатной остановки.</summary>
    public const string StoppedMarker = "Stopped the server!";

    /// <summary>Остановка из панели хостера: «Server termination event SIGTERM received».</summary>
    public const string TerminationMarker = "Server termination event";

    /// <summary>Имя папки запуска в Logs/Archive: время его начала по часам сервера.</summary>
    public const string RunNameFormat = "yyyy-MM-dd_HH_mm_ss";

    // дата — в формате системы сервера: у G-Portal (Linux) «17.10.2026 13:27:08», у английской Windows «10/17/2026 1:27:08 PM»
    private static readonly string[] TimeFormats =
    [
        "d.M.yyyy H:mm:ss", "M/d/yyyy h:mm:ss tt", "d/M/yyyy H:mm:ss", "M/d/yyyy H:mm:ss", "yyyy-MM-dd H:mm:ss",
        "yyyy/M/d H:mm:ss", "d-M-yyyy H:mm:ss",
    ];

    public bool FromMiddle { get; } = fromMiddle;

    /// <summary>Время первой строки — начало запуска (время сервера); чтение с середины — null.</summary>
    public DateTime? Started { get; private set; }

    /// <summary>Время первой прочитанной строки с отметкой (время сервера) — и при чтении с середины: запуск начался не позже.</summary>
    public DateTime? FirstTime { get; private set; }

    /// <summary>Время последней строки с отметкой времени (время сервера).</summary>
    public DateTime? LastTime { get; private set; }

    // shortcut: с середины огромного лога строки готовности не видно — считаем сервер запущенным (сотни КБ лога
    // набираются уже после запуска). Переделать, если встретятся огромные логи ещё на запуске
    public bool Ready { get; private set; } = fromMiddle;

    public bool StopBegun { get; private set; }

    public bool Stopped { get; private set; }

    public bool Crashed { get; private set; }

    /// <summary>Имя, под которым запуск ляжет в архив («2026-10-17_13_27_08»); начала не видно — null.</summary>
    public string? RunName => Started?.ToString(RunNameFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Учесть строку. Возвращает её время, а у строки без отметки (стек, продолжение записи) — время предыдущей.
    /// Строки чата не в счёт: их пишут игроки, и «Stopped the server!» в чате — не остановка. Мост чата (Discord и т. п.)
    /// пишет те же слова обычной записью мода, поэтому остановку и вылет узнаём по тексту записи целиком и её уровню:
    /// «[Event] Stopped the server!» ровно, остановка начата — запись с этих слов, вылет — запись уровня Error или Fatal.
    /// </summary>
    public DateTime? Add(string line)
    {
        if (GameLog.Parse([line]) is not [var entry])
        {
            // продолжение записи — стек или отчёт о вылете: в нём сервер называет мод и пишет, куда сохранил отчёт
            if (CrashAnalyzer.IsServerCrashLine(line)) Crashed = true;
            return LastTime;
        }
        if (ParseTime(entry.Time) is { } time)
        {
            FirstTime ??= time;
            if (!FromMiddle) Started ??= time;
            LastTime = time;
        }
        if (entry.Level == "Chat") return LastTime;
        var text = entry.Message;
        if (entry.Level == "Fatal" || entry.Level == "Error" && CrashAnalyzer.IsServerCrashLine(text)) Crashed = true;
        if (text.Contains(ServerHost.ReadyMarker, StringComparison.Ordinal)) Ready = true;
        if (text.StartsWith(ServerHost.StoppingMarker, StringComparison.Ordinal) || text.StartsWith(TerminationMarker, StringComparison.Ordinal))
            StopBegun = true;
        if (entry.Level == "Event" && text.TrimEnd() == StoppedMarker) Stopped = true;
        return LastTime;
    }

    /// <summary>
    /// Состояние по признакам. Лог есть, но пуст или без строки готовности — Starting. <paramref name="stale"/> — лог
    /// давно не растёт: начатая остановка тогда считается законченной (сервер убили посреди неё).
    /// </summary>
    public HostedServerState State(bool stale) =>
        Crashed ? HostedServerState.Crashed
        : Stopped ? HostedServerState.Stopped
        : StopBegun ? stale ? HostedServerState.Stopped : HostedServerState.Stopping
        : Ready ? HostedServerState.Running
        : HostedServerState.Starting;

    /// <summary>Время из начала строки лога («17.10.2026 13:27:08») — время сервера без пояса; не разобрать — null.</summary>
    public static DateTime? ParseTime(string text) =>
        DateTime.TryParseExact(text.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

    /// <summary>Начало запуска по имени его папки в архиве («2026-10-17_13_25_13»).</summary>
    public static bool TryParseRunName(string name, out DateTime started) =>
        DateTime.TryParseExact(name, RunNameFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out started);
}

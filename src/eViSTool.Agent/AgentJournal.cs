using eViSTool.Core.Server;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Журнал действий: кто и что менял через агента (локальный вход и удалённый). Старое чистится при первой записи
/// и дальше раз в сутки.
/// </summary>
internal sealed class AgentJournal(AgentOptions opts)
{
    private DateTime _prunedAt = DateTime.MinValue;

    public string FilePath { get; } = ServerJournal.FileFor(opts.ProfileId, opts.AgentsDir);

    /// <summary>
    /// Записать действие: кто (имя из заголовка окна), откуда (адрес — только для удалённого входа). at — когда пришёл
    /// запрос: запись делается после ответа, и следующий запрос того же окна мог успеть записаться раньше; по времени
    /// запроса журнал идёт в порядке действий.
    /// </summary>
    public void Write(string action, string[] args, HttpContext ctx, bool isRemote, DateTime? at = null)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is { IsIPv4MappedToIPv6: true }) ip = ip.MapToIPv4();
        var who = Uri.UnescapeDataString(ctx.Request.Headers[ServerJournal.ClientHeader].ToString());
        try
        {
            if (DateTime.UtcNow - _prunedAt > TimeSpan.FromDays(1))
            {
                _prunedAt = DateTime.UtcNow;
                ServerJournal.Prune(FilePath, DateTime.UtcNow);
            }
            ServerJournal.Append(FilePath, new JournalEntry(at ?? DateTime.UtcNow, action, args, who.Length > 0 ? who : null,
                isRemote ? ip?.ToString() : null, isRemote));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // журнал — справка: не записался, но действие уже сделано, отвечаем как обычно
        }
    }
}

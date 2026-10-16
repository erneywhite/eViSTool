using eViSTool.Core.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

internal sealed partial class AgentApi
{
    // статистика сервера: отчёт за период, включить или выключить сбор, стереть собранное
    private void MapStats(WebApplication web)
    {
        web.MapGet("/stats", (HttpContext ctx) =>
            Enum.TryParse<StatsPeriod>(ctx.Request.Query["period"].ToString(), out var period)
                ? Guard(() => Files.Stats.Report(period, DateTime.Now)) : Results.BadRequest());
        web.MapPost("/stats/enabled", async (HttpContext ctx) =>
            await ReadBody<StatsToggle>(ctx) is { } toggle ? Guard(() => { Files.Stats.SetEnabled(toggle.Enabled); return Status(); }) : Results.BadRequest());
        web.MapPost("/stats/clear", () => Guard(() => { Files.Stats.Clear(); return Status(); }));
    }
}

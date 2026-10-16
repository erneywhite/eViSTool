using eViSTool.Core.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

internal sealed partial class AgentApi
{
    // игроки: списки — всегда; правка файлов — только у остановленного (работающий держит списки в памяти и перезапишет их)
    private void MapPlayers(WebApplication web)
    {
        web.MapGet("/players", () => Guard(() => PlayerLists.Read()));
        web.MapPost("/players/edit", async (HttpContext ctx) =>
            await ReadBody<PlayerFileEdit>(ctx) is { } edit
                ? WhenStopped(() =>
                {
                    var before = PlayerLists.Read();
                    PlayerLists.Apply(edit);
                    Note(ctx, JournalDetails.Players(before, edit));
                    return Status();
                })
                : Results.BadRequest());
    }
}

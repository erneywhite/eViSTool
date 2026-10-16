using eViSTool.Core.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

internal sealed partial class AgentApi
{
    // что меняет сервер — в журнал, если получилось (ответ 2xx); тело запроса читаем заранее: обработчик его закроет
    private void UseJournal(WebApplication web, bool isRemote)
    {
        web.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            if (HttpMethods.IsGet(ctx.Request.Method) || ServerJournal.Describe(ctx.Request.Method, path, null, null) is null)
            {
                await next();
                return;
            }
            string? body = null;
            if (ServerJournal.NeedsBody(path))
            {
                ctx.Request.EnableBuffering();
                using (var reader = new StreamReader(ctx.Request.Body, leaveOpen: true)) body = await reader.ReadToEndAsync();
                ctx.Request.Body.Position = 0;
            }
            await next();
            if (ctx.Response.StatusCode is >= 200 and < 300
                && ServerJournal.Describe(ctx.Request.Method, path, body, ctx.Request.Headers[AgentClient.ModFileHeader].ToString()) is { } act)
                journal.Write(act.Action, act.Args, ctx, isRemote);
        });
    }

    // журнал действий для окна: кто и что менял
    private void MapJournal(WebApplication web)
    {
        web.MapGet("/journal", () => Guard(() => ServerJournal.Read(journal.FilePath, DateTime.UtcNow)));
    }
}

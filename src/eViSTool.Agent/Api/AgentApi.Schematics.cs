using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

internal sealed partial class AgentApi
{
    // постройки WorldEdit сервера (папка WorldEdit в папке данных): список, файл целиком, запись, удаление — окно на
    // другой машине переносит их между профилями. Имя — только имя файла .json, без пути (Schematics.PathOf)
    private void MapSchematics(WebApplication web)
    {
        web.MapGet("/schematics", () => Guard(() => Schematics.List(Opts.DataPath)));
        // миры сервера (Saves/*.vcdbs) — выбор мира и создание нового в редакторе конфига окна на другой машине
        web.MapGet("/worlds", () => Guard(() => ServerWorlds.List(Opts.DataPath)));
        web.MapPost("/schematics/read", async (HttpContext ctx) =>
        {
            if (await ReadBody<SchematicNameRequest>(ctx) is not { } r) return Results.BadRequest();
            ctx.Items[ServerJournal.SkipItem] = true; // чтение — не действие
            try
            {
                return Results.Bytes(Schematics.Read(Opts.DataPath, r.Name), "application/octet-stream");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return Problem(ex.Message);
            }
        });
        web.MapPost("/schematics/write", async (HttpContext ctx) =>
        {
            var name = Uri.UnescapeDataString(ctx.Request.Headers[AgentClient.ModFileHeader].ToString());
            using var body = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(body, ctx.RequestAborted);
            return Guard(() => { Schematics.Write(Opts.DataPath, name, body.ToArray()); return new { }; });
        });
        web.MapPost("/schematics/delete", async (HttpContext ctx) =>
            await ReadBody<SchematicNameRequest>(ctx) is { } r
                ? Guard(() => { Schematics.Delete(Opts.DataPath, r.Name); return new { }; })
                : Results.BadRequest());
    }
}

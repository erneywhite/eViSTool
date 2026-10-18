using eViSTool.Core.Mods;
using eViSTool.Core.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;

internal sealed partial class AgentApi
{
    // моды сервера — для окна на другой машине: список, включение/выключение, удаление, установка присланного архива
    private void MapMods(WebApplication web)
    {
        web.MapGet("/mods", () => Guard(Mods.List));
        web.MapPost("/mods/toggle", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var request = JsonConvert.DeserializeObject<ModToggleRequest>(await reader.ReadToEndAsync());
            return request is null ? Results.BadRequest() : Guard(() => { Mods.SetEnabled(request.Path, request.Enabled); return Status(); });
        });
        web.MapPost("/mods/delete", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var request = JsonConvert.DeserializeObject<ModPathRequest>(await reader.ReadToEndAsync());
            using var history = HistoryOp(ctx);
            return request is null ? Results.BadRequest() : Guard(() => { Mods.Delete(request.Path); return Status(); });
        });
        // история изменений модов сервера (записи и отметки запусков; в строки-сеансы их собирает окно)
        web.MapGet("/mods/history", () => Guard(() => ModHistory.ReadEntries(ModHistory.FileFor(Opts.ProfileId))));
        web.MapPost("/mods/install", async (HttpContext ctx) =>
        {
            // имя файла — только имя, без пути: мод ляжет в папку модов под ним
            var name = Path.GetFileName(Uri.UnescapeDataString(ctx.Request.Headers[AgentClient.ModFileHeader].ToString()));
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.Length <= 4) return Results.BadRequest();
            var dir = Path.Combine(Path.GetTempPath(), "evistool-upload-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                var zip = Path.Combine(dir, name);
                await using (var file = File.Create(zip))
                    await ctx.Request.Body.CopyToAsync(file, ctx.RequestAborted);
                using var history = HistoryOp(ctx);
                // архив с ModDB — окно говорит, какой это релиз: версия в файле не та — отметка «файл релиза»
                var release = AgentClient.ParseReleaseHeader(ctx.Request.Headers[AgentClient.ReleaseHeader].ToString());
                return Guard(() =>
                {
                    var result = Mods.Install(zip, release);
                    Note(ctx, [new JournalDetail("modVersion", [result.Name, result.Version ?? "", result.OldVersion ?? ""])]);
                    return result;
                });
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }
        });
    }
}

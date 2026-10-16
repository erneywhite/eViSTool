using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

internal sealed partial class AgentApi
{
    // настройки модов сервера: список, чтение, запись (с проверкой, не поменяли ли файл), шаг назад, сброс
    private void MapModConfig(WebApplication web)
    {
        web.MapGet("/modconfig", () => Guard(ModConfigs.List));
        web.MapPost("/modconfig/read", async (HttpContext ctx) =>
            await ReadBody<ModConfigPathRequest>(ctx) is { } r ? Guard(() => ModConfigs.Read(r.Path)) : Results.BadRequest());
        web.MapPost("/modconfig/save", async (HttpContext ctx) =>
            await ReadBody<ModConfigSaveRequest>(ctx) is { } r ? Guard(() =>
            {
                var before = ModConfigs.Read(r.Path).Text;
                var result = ModConfigs.Save(r);
                if (!result.Changed) ctx.Items[ServerJournal.SkipItem] = true;
                else NoteJson(ctx, before, result.Content?.Text ?? r.Text);
                return result;
            }) : Results.BadRequest());
        web.MapPost("/modconfig/undo", async (HttpContext ctx) =>
            await ReadBody<ModConfigPathRequest>(ctx) is { } r ? Guard(() =>
            {
                var before = ModConfigs.Read(r.Path).Text;
                var result = ModConfigs.Undo(r.Path);
                NoteJson(ctx, before, result.Text);
                return result;
            }) : Results.BadRequest());
        web.MapPost("/modconfig/reset", async (HttpContext ctx) =>
            await ReadBody<ModConfigPathRequest>(ctx) is { } r ? Guard(() =>
            {
                var before = ModConfigs.Read(r.Path).Text;
                var result = ModConfigs.Reset(r.Path);
                NoteJson(ctx, before, result.Text);
                return result;
            }) : Results.BadRequest());
    }
}

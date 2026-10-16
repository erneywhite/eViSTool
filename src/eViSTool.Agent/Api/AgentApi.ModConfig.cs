using eViSTool.Core.Profiles;
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
            await ReadBody<ModConfigSaveRequest>(ctx) is { } r ? Guard(() => ModConfigs.Save(r)) : Results.BadRequest());
        web.MapPost("/modconfig/undo", async (HttpContext ctx) =>
            await ReadBody<ModConfigPathRequest>(ctx) is { } r ? Guard(() => ModConfigs.Undo(r.Path)) : Results.BadRequest());
        web.MapPost("/modconfig/reset", async (HttpContext ctx) =>
            await ReadBody<ModConfigPathRequest>(ctx) is { } r ? Guard(() => ModConfigs.Reset(r.Path)) : Results.BadRequest());
    }
}

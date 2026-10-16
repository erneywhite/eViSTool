using eViSTool.Core.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;

internal sealed partial class AgentApi
{
    // сервер и его консоль: статус, запуск и остановка, команды
    private void MapServer(WebApplication web)
    {
        web.MapGet("/status", () => Json(Status()));

        web.MapGet("/console", async (long since, int? wait, HttpContext ctx) =>
        {
            var lines = await Host.Console.WaitSinceAsync(since, TimeSpan.FromSeconds(Math.Clamp(wait ?? 0, 0, 30)), ct: ctx.RequestAborted);
            return Json(lines);
        });

        web.MapPost("/start", () => Run(Host.StartAsync));
        // остановка может идти минуты (большой мир сохраняется) — отвечаем сразу, окно следит за статусом
        web.MapPost("/stop", () => Run(() => { _ = Host.StopAsync(); return Task.CompletedTask; }));
        web.MapPost("/restart", () => Run(() => { _ = Host.RestartAsync(); return Task.CompletedTask; }));
        web.MapPost("/kill", () => Run(() => { Host.Kill(); return Task.CompletedTask; }));
        web.MapPost("/command", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var req = JsonConvert.DeserializeObject<CommandRequest>(await reader.ReadToEndAsync());
            if (string.IsNullOrWhiteSpace(req?.Text)) return Results.BadRequest();
            return await Run(() => Host.SendCommandAsync(req.Text.Trim()));
        });

        // команды сервера для подсказок в консоли окна
        web.MapGet("/commands", () => Json(commands.Sorted()));
    }

    // выключить агента — только с этой машины
    private void MapShutdown(WebApplication web)
    {
        web.MapPost("/shutdown", () =>
        {
            state.Shutdown.Cancel(); // сервер остановим при выходе (finally в Program.cs)
            return Json(Status());
        });
    }
}

using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;

internal sealed partial class AgentApi
{
    // serverconfig.json — для окна на другой машине; писать можно только в остановленный сервер (иначе он перезапишет файл)
    private void MapConfig(WebApplication web)
    {
        web.MapGet("/config", () => Guard(Files.ReadConfig));
        web.MapPut("/config", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var request = JsonConvert.DeserializeObject<ConfigSaveRequest>(await reader.ReadToEndAsync());
            return request is null ? Results.BadRequest() : WhenStopped(() => Files.WriteConfig(request));
        });
        web.MapPost("/config/generate", async () =>
        {
            if (Host.State != ServerState.Stopped)
                return Problem(Loc.T("sched.needStopped"));
            try
            {
                await ServerConfigGenerator.GenerateAsync(Opts.ExePath, Opts.DataPath);
                return Json(Files.ReadConfig());
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                return Problem(ex.Message);
            }
        });
    }
}

using eViSTool.Core.Localization;
using eViSTool.Core.Notifications;
using eViSTool.Core.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;

internal sealed partial class AgentApi
{
    // оповещения и объявления по расписанию
    private void MapNotify(WebApplication web)
    {
        // оповещения: окну на другой машине — без секретов; от него — с секретами (внутри TLS), шифруем их здесь
        web.MapGet("/notify", () => Json(ServerNotifySettings.Load(Opts.ProfileId, Opts.AgentsDir).WithoutSecrets()));
        web.MapPut("/notify", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var upload = JsonConvert.DeserializeObject<ServerNotifyUpload>(await reader.ReadToEndAsync());
            return upload is null ? Results.BadRequest() : Guard(() => { upload.ToSettings().Save(Opts.ProfileId, Opts.AgentsDir); return Status(); });
        });
        web.MapPost("/notify/test", async () => Json(await notify.TestAsync(
            Loc.T("notify.testTitle"), AgentNotifications.Line("◇", "notify.lbl.from", Loc.T("notify.testFromServer", Environment.MachineName)))));

        web.MapGet("/announcements", () => Json(ServerAnnouncements.Load(Opts.ProfileId, Opts.AgentsDir)));
        web.MapPut("/announcements", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var settings = JsonConvert.DeserializeObject<ServerAnnouncements>(await reader.ReadToEndAsync());
            return settings is null ? Results.BadRequest() : Guard(() => { settings.Save(Opts.ProfileId, Opts.AgentsDir); return Status(); });
        });
    }
}

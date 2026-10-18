using eViSTool.Core.AppUpdate;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Core.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

internal sealed partial class AgentApi
{
    // обновить eViSTool на этом компьютере до версии окна — кнопка «Обновить там» у окна на ДРУГОМ компьютере,
    // поэтому точка есть и на удалённом входе (до 0.9.1 её там не было, и кнопка отвечала «не умеет обновляться»)
    private void MapSelfUpdate(WebApplication web)
    {
        web.MapPost("/self-update", async (HttpContext ctx) =>
        {
            var version = state.Version;
            if (await ReadBody<SelfUpdateRequest>(ctx) is not { } request
                || !ModVersion.TryParse(request.Version, out var target)) return Results.BadRequest();
            // в папке может стоять другая версия, чем у этого агента: после «eViSTool.Agent rollback» он работает на
            // новой до перезапуска, а в папке уже прежняя, — на новую тогда обновить можно
            var installed = AppRollback.InstalledVersion(AppContext.BaseDirectory) ?? version;
            if (request.Version == version && request.Version == installed) return Problem(Loc.T("selfupd.same"), StatusCodes.Status400BadRequest);
            if (!request.AllowDowngrade && ModVersion.TryParse(version, out var current) && target < current)
                return Problem(Loc.T("selfupd.downgrade", target, version), StatusCodes.Status400BadRequest);
            if (selfUpdate.Busy) return Problem(Loc.T("selfupd.busy"));
            selfUpdate.Begin(target);
            return Json(Status());
        });
    }
}

using eViSTool.Core.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;

internal sealed partial class AgentApi
{
    // копии мира и расписание — нужны окну на другой машине (своё окно работает с файлами напрямую)
    private void MapBackups(WebApplication web)
    {
        // копия мира сейчас (кнопка в окне); на остановленном сервере копию делает само окно
        web.MapPost("/backup", () => Run(schedule.RequestBackup));

        web.MapGet("/automation", () => Json(Files.LoadAutomation()));
        web.MapPut("/automation", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var settings = JsonConvert.DeserializeObject<ServerAutomation>(await reader.ReadToEndAsync());
            return settings is null ? Results.BadRequest() : Guard(() =>
            {
                var before = JsonConvert.SerializeObject(Files.LoadAutomation());
                Files.SaveAutomation(settings);
                NoteJson(ctx, before, JsonConvert.SerializeObject(settings));
                return Status();
            });
        });

        web.MapGet("/backups", () => Guard(Files.ListBackups));
        web.MapPost("/backups/copy", () => WhenStopped(() => Files.CopyWorld(DateTime.Now)));
        web.MapPost("/backups/restore", async (HttpContext ctx) =>
            await ReadName(ctx) is { Length: > 0 } name
                ? WhenStopped(() =>
                {
                    // мир возвращают вручную — сервер, упавший перед этим, не должен подняться сам (сторож ждал паузу)
                    Host.CancelPendingRestart();
                    return Files.Restore(name, DateTime.Now);
                })
                : Results.BadRequest());

        // обзор папок для окна на другом компьютере (выбор папки для копий): только имена папок, начать — с данных сервера
        web.MapPost("/fs/dirs", async (HttpContext ctx) =>
        {
            var path = await ReadName(ctx);
            return Guard(() => FolderBrowser.List(path, Opts.DataPath));
        });
        // тело блоком с return: лямбда-выражение из одного вызова подходит под RequestDelegate, и ответ выбрасывается — окно
        // получало пустой 200 и писало «empty response» (так было с 0.9.0: своя папка для копий через агента не сохранялась)
        web.MapPost("/backups/check-dir", async (HttpContext ctx) =>
        {
            var dir = await ReadName(ctx);
            return Json(new BackupDirCheck(await Task.Run(() => BackupStore.CheckDir(dir))));
        });
        web.MapPost("/backups/delete", async (HttpContext ctx) =>
            await ReadName(ctx) is { Length: > 0 } name ? Guard(() => { Files.DeleteBackup(name); return Status(); }) : Results.BadRequest());
    }
}

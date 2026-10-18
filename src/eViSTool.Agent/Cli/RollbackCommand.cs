using eViSTool.Core.AppUpdate;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;

/// <summary>
/// Команда «rollback»: вернуть прежнюю версию eViSTool в этой папке, пока она хранится после обновления
/// (<see cref="AppRollback"/>), — как «Вернуться на прежнюю версию» в окне. Файлы меняются местами; агент, который уже
/// работает, остаётся на новой версии до перезапуска: сервер ради этого не останавливаем.
/// </summary>
internal static class RollbackCommand
{
    public static int Run(AgentArgs cli, string[] args)
    {
        if (cli.Help) return Commands.Usage(Commands.Ok);
        var w = CliWords.Parse(args);
        if (w.Error is not null || w.TooMany(0)) return w.Bad(Loc.T("agent.usage"));

        var appDir = AppContext.BaseDirectory;
        var version = AgentProtocol.AppVersion;
        if (AppRollback.Available(appDir, version) is null)
        {
            Console.Error.WriteLine(Loc.T("agent.rollbackNone", version));
            return Commands.Failed;
        }
        string previous;
        try
        {
            previous = AppRollback.RollBack(appDir, version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // файлы вернулись как были
            Console.Error.WriteLine(Loc.T("agent.rollbackFailed", ex.Message));
            return Commands.Failed;
        }
        Console.WriteLine(Loc.T("agent.rollbackDone", previous, version));
        Console.WriteLine(Loc.T("agent.rollbackRestart", version));
        return Commands.Ok;
    }
}

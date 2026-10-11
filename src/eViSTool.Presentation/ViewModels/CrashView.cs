using eViSTool.Core.Diagnostics;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Что показать в окне вылета — для игры (разбор её логов) и для сервера (разбор агента).</summary>
public sealed record CrashView(string Title, string ProfileName, string? ModId, string? ModName, string? ModVersion, CulpritSource? Source,
    bool ModInstalled, string Reason, int ErrorCount, string? Error, IReadOnlyList<string> Stack)
{
    public static CrashView ForGame(GameProfile profile, CrashFinding f) =>
        new(Loc.T(f.Kind == GameExitKind.Crashed ? "crash.titleCrashed" : "crash.titleWorld"), profile.Name,
            f.Culprit?.ModId, f.Culprit?.Mod?.Name ?? f.Culprit?.ModId, f.Culprit?.Version, f.Culprit?.Source, f.Culprit?.Mod is not null,
            f.Reason, f.ErrorCount, f.Error, f.Stack);

    public static CrashView ForServer(GameProfile profile, ServerCrashInfo c) =>
        new(Loc.T("crash.titleServer", profile.Name), profile.Name, c.ModId, c.ModName, c.ModVersion, c.Source, c.ModPath is not null,
            c.Reason, c.ErrorCount, c.Error, c.Stack);
}

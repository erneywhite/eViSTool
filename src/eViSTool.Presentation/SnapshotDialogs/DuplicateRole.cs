using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Окно «Дублировать роль»: открытое (dialog.duplicaterole) и с занятым кодом (dialog.duplicaterole.taken). Роли — из
/// стандартного списка сервера (как в серверном конфиге стенда), копируется «Survival Player». Окно только открывается:
/// «Создать копию» на снимке никто не нажимает.
/// </summary>
internal static class DuplicateRole
{
    private static readonly string[] Codes =
        ["suvisitor", "crvisitor", "limitedsuplayer", "limitedcrplayer", "suplayer", "crplayer", "sumod", "crmod", "admin"];

    /// <summary>Только что открытое окно: код подобран свободный (suplayer2), название — «Survival Player (копия)».</summary>
    [SnapshotDialog("duplicaterole")]
    private static void Open(MainViewModel main) => AppWindows.Current.DuplicateRole("suplayer", "Survival Player", Codes);

    /// <summary>
    /// В поле код, который уже есть в списке («admin»): под полем — ошибка, «Создать копию» выключена.
    /// </summary>
    [SnapshotDialog("duplicaterole.taken")]
    private static void OpenTaken(MainViewModel main)
    {
        DuplicateRoleViewModel.SnapshotCode = "admin";
        AppWindows.Current.DuplicateRole("suplayer", "Survival Player", Codes);
    }
}

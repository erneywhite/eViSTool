using eViSTool.Core.AppUpdate;
using eViSTool.Core.Localization;
using eViSTool.Core.Versioning;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Окно «Вышла новая версия»: обычное обновление (dialog.update), рекомендуемое (dialog.update.recommended) и со строкой
/// состояния (dialog.update.status). Релизы
/// выдуманные (9.9.9 и старше), без сети. Модель «О программе» — своя, а не main.About: фоновая проверка при запуске
/// пишет в main.About и сбросила бы найденное (экран about.update из-за неё ждёт), а окно читает версии при открытии.
/// «Пропустить» на снимке ничего не запоминает.
/// </summary>
internal static class Update
{
    [SnapshotDialog("update")]
    private static void Open(MainViewModel main) => AppWindows.Current.ShowUpdatePrompt(About(recommended: false), () => { });

    [SnapshotDialog("update.recommended")]
    private static void OpenRecommended(MainViewModel main) => AppWindows.Current.ShowUpdatePrompt(About(recommended: true), () => { });

    /// <summary>
    /// Строка состояния над кнопками (dialog.update.status): окно показывает её, только когда она меняется при открытом
    /// окне, — поэтому после открытия. Установку не изображаем: кнопки в окне на WPF выключает только своё нажатие.
    /// </summary>
    [SnapshotDialog("update.status")]
    private static void OpenWithStatus(MainViewModel main)
    {
        var about = About(recommended: false);
        AppWindows.Current.ShowUpdatePrompt(about, () => { });
        about.UpdateStatus = Loc.T("about.updateDownloading", 40);
    }

    private static AboutViewModel About(bool recommended)
    {
        var about = new AboutViewModel();
        about.UseReleases(
        [
            Release("9.9.9", new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero), """
                **eViSTool 9.9.9: mod history and faster catalog.**

                **New**
                - Mod history: the History tab in My mods.
                - The catalog loads twice as fast.

                **Fixes**
                - Server console no longer loses the last line.

                ---

                История модов и быстрый каталог.

                **Новое**
                - История модов: вкладка «История» в «Моих модах», откат к любой прошлой версии.
                - Каталог модов открывается вдвое быстрее, картинки подгружаются по мере прокрутки.

                **Исправления**
                - Консоль сервера больше не теряет последнюю строку.
                - Окно настроек мира не сбрасывает выбранную вкладку после сохранения.
                """),
            Release("9.9.8", new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero), (recommended ? "<!-- evistool: recommended -->\n" : "") + """
                **eViSTool 9.9.8: safer server updates.**

                **Fixes**
                - The agent keeps the server running while it updates.

                ---

                Надёжное обновление сервера.

                **Исправления**
                - Агент не останавливает сервер, пока обновляется сам.
                - Копия мира перед обновлением модов теперь проверяется на целостность.
                """),
            Release("9.9.7", new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero), ""), // лента без описаний — ссылка на страницу
        ]);
        return about;
    }

    private static AppRelease Release(string version, DateTimeOffset published, string notes) => new()
    {
        Version = ModVersion.ParseOrNull(version)!,
        Tag = "v" + version,
        PageUrl = "https://example.invalid/releases/v" + version,
        Notes = notes,
        Published = published,
        AssetName = $"eViSTool-{version}-win-x64.zip",
        AssetUrl = $"https://example.invalid/eViSTool-{version}-win-x64.zip",
    };
}

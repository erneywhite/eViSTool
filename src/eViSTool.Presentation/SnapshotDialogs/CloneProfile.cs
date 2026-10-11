using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;
using Newtonsoft.Json.Linq;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Окно клона серверного профиля: клон «Стенда» (dialog.cloneprofile), он же при запущенном сервере
/// (dialog.cloneprofile.running) и импорт существующих данных сервера (dialog.cloneprofile.import). Окно только
/// открывается: план читает папку данных, «Клонировать» снимки не нажимают.
/// </summary>
internal static class CloneProfile
{
    /// <summary>Клон «Стенда»: один конфиг (выбора мира нет), резервных копий нет, план посчитан.</summary>
    [SnapshotDialog("cloneprofile")]
    private static void Clone(MainViewModel main) => AppWindows.Current.CloneServerProfile(Stand(main), serverRunning: false);

    /// <summary>Сервер «Стенда» запущен: предупреждение сверху, «Клонировать» выключена.</summary>
    [SnapshotDialog("cloneprofile.running")]
    private static void Running(MainViewModel main) => AppWindows.Current.CloneServerProfile(Stand(main), serverRunning: true);

    /// <summary>
    /// Импорт данных сервера, как из окна «Новый профиль»: в папке два конфига (выбор мира) и резервные копии (галочка).
    /// Папка собирается во временной папке системы из конфига «Стенда» — сам стенд и данные пользователя не трогаются.
    /// </summary>
    [SnapshotDialog("cloneprofile.import")]
    private static void Import(MainViewModel main)
    {
        var from = ImportData(Stand(main));
        var source = new GameProfile { Name = Path.GetFileName(from), Kind = ProfileKind.Server, GameDir = Stand(main).GameDir, DataDir = from };
        AppWindows.Current.CloneServerProfile(source, serverRunning: false, Loc.T("profile.newServer", main.Profiles.Count + 1));
    }

    private static GameProfile Stand(MainViewModel main) =>
        main.ActiveProfile?.Model ?? throw new InvalidOperationException("Для снимка нужен активный профиль стенда");

    /// <summary>
    /// «Существующие данные сервера»: конфиг стенда (мир «Test World» с сохранением), второй конфиг с другим миром и
    /// резервные копии. Каждый раз — заново, чтобы снимки были одинаковыми.
    /// </summary>
    private static string ImportData(GameProfile stand)
    {
        var root = Path.Combine(Path.GetTempPath(), "evistool-snap-import");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var data = Directory.CreateDirectory(Path.Combine(root, "VintagestoryData")).FullName;

        File.Copy(Path.Combine(stand.DataDir!, ProfileCloner.MainConfig), Path.Combine(data, ProfileCloner.MainConfig));
        File.WriteAllText(Path.Combine(data, "serverconfig-creative.json"), new JObject
        {
            ["ServerName"] = "Творческий сервер",
            ["WorldConfig"] = new JObject { ["WorldName"] = "Творческий мир", ["SaveFileLocation"] = "Saves/creative.vcdbs" },
        }.ToString());

        Blank(Path.Combine(data, "Saves", "default.vcdbs"), 3_400_000);
        Blank(Path.Combine(data, "Saves", "creative.vcdbs"), 860_000);
        Blank(Path.Combine(data, "Backups", "default-2026-10-01_21-00-00.vcdbs"), 1_250_000);
        Directory.CreateDirectory(Path.Combine(data, "Mods"));
        return data;
    }

    /// <summary>Файл нужного размера из нулей (содержимое окну не важно, только размер).</summary>
    private static void Blank(string path, long bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        file.SetLength(bytes);
    }
}

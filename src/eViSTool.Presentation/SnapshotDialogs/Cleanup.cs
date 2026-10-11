using eViSTool.Core;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Окно «Уборка диска»: серверный профиль стенда (dialog.cleanup) — распакованные моды, старые логи, данные удалённого
/// мода и мира, настройки без мода, сохранённые версии модов и конфигов, недокачанная загрузка; клиентский профиль
/// (dialog.cleanup.client) — моды серверов (один сервер давний и отмечен, другой свежий — галочка вида «частично») и
/// карты миров; клиентский профиль с пустой папкой — «Убирать нечего» (dialog.cleanup.empty). Мусор раскладывается во
/// временной папке стенда (build/snapshot.ps1): в его vsdata, в данных программы рядом с exe и в папках клиентов рядом
/// с vsdata. Файлы пустые: размер задаётся длиной, а не содержимым. Окно только открывается: «Убрать в корзину» на
/// снимке никто не нажимает.
/// </summary>
internal static class Cleanup
{
    private const string ClientFolder = "snapshot-cleanup-client";

    [SnapshotDialog("cleanup")]
    private static void Server(MainViewModel main)
    {
        var stand = Stand(main);
        var root = StandRoot(stand);
        var data = stand.DataDir!;
        var app = AppPaths.Root; // проверено в StandRoot: data рядом с exe сборки стенда
        if (!PathRules.IsUnder(data, root) || !PathRules.IsUnder(app, root))
            throw new InvalidOperationException("Снимок уборки: папки стенда не во временной папке стенда");

        // что копит игра (сервер): распакованные моды, логи прошлых запусков, данные модов
        Put(Path.Combine(data, "Cache", "unpack", "primitivesurvival_3.7.6", "assets.bin"), 38_421_504, Day(2026, 10, 9));
        Put(Path.Combine(data, "Cache", "unpack", "primitivesurvival_3.7.5", "assets.bin"), 37_986_304, Day(2026, 9, 2));
        Put(Path.Combine(data, "Cache", "unpack", "carryon_1.8.0", "carryon.dll"), 1_146_880, Day(2026, 10, 9));
        Put(Path.Combine(data, "Logs", "Archive", "server-main-2026-09-28.log"), 6_291_456, Day(2026, 9, 28));
        Put(Path.Combine(data, "Logs", "Archive", "server-debug-2026-09-28.log"), 5_767_168, Day(2026, 9, 28));
        Put(Path.Combine(data, "Logs", "Archive", "server-main-2026-10-04.log"), 2_097_152, Day(2026, 10, 4));
        Put(Path.Combine(data, "ModData", "expandedfoods", "recipes-cache.json"), 2_516_582, Day(2026, 8, 17));
        Put(Path.Combine(data, "ModData", "betterruins", "placed.dat"), 409_600, Day(2026, 7, 30));
        Put(Path.Combine(data, "ModData", "3f9c2a71-5d4e-4b8a-9e61-0c7d2b8f4a13", "primitivesurvival", "traps.json"), 6_606_028, Day(2026, 10, 9));
        Put(Path.Combine(data, "ModConfig", "expandedfoods.json"), 3_174, Day(2026, 8, 17));

        // что копит eViSTool для этого профиля: версии модов и конфигов, недокачанная загрузка
        Put(Path.Combine(app, "ModBackups", stand.Id, "expandedfoods", "expandedfoods_1.6.2.zip"), 4_299_161, Day(2026, 8, 17));
        Put(Path.Combine(app, "ModBackups", stand.Id, "carryon", "carryon_1.7.4.zip"), 311_296, Day(2026, 9, 12));
        Put(Path.Combine(app, "ModConfigBackups", stand.Id, "carryon.json.2026-09-12T18-40-03"), 91, Day(2026, 9, 12));
        Put(Path.Combine(app, "Downloads", "primitivesurvival_3.8.0.zip.part"), 12_582_912, Day(2026, 10, 10));

        AppWindows.Current.ShowCleanup(stand);
    }

    /// <summary>Клиентский профиль: моды серверов (давний сервер отмечен, свежий — нет) и карты миров.</summary>
    [SnapshotDialog("cleanup.client")]
    private static void Client(MainViewModel main)
    {
        var stand = Stand(main);
        var dir = Path.Combine(StandRoot(stand), ClientFolder);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); // прошлый снимок мог оставить свою

        var now = DateTime.UtcNow;
        Put(Path.Combine(dir, "Cache", "unpack", "carryon_1.8.0", "carryon.dll"), 1_146_880, Day(2026, 10, 9));
        Put(Path.Combine(dir, "Cache", "unpack", "primitivesurvival_3.7.6", "assets.bin"), 38_421_504, Day(2026, 10, 9));
        // давний сервер — больше месяца назад (отмечен), свежий — на днях (не отмечен)
        Put(Path.Combine(dir, "ModsByServer", "178.172.138.142-42420", "Mods", "expandedfoods_1.6.2.zip"), 4_299_161, Day(2026, 7, 21));
        Put(Path.Combine(dir, "ModsByServer", "178.172.138.142-42420", "Mods", "betterruins_0.4.9.zip"), 18_874_368, Day(2026, 7, 21));
        Put(Path.Combine(dir, "ModsByServer", "95.216.40.12-42420", "Mods", "carryon_1.8.0.zip"), 311_296, now.AddDays(-2));
        Put(Path.Combine(dir, "Logs", "Archive", "client-main-2026-10-01.log"), 3_145_728, Day(2026, 10, 1));
        // карты: файлы одного мира — одним пунктом
        Put(Path.Combine(dir, "Maps", "c8a1e4f0-7b2d-4f3a-a6c9-51e0d7b3f2a4.db"), 22_020_096, Day(2026, 10, 9));
        Put(Path.Combine(dir, "Maps", "c8a1e4f0-7b2d-4f3a-a6c9-51e0d7b3f2a4-geology.db"), 3_670_016, Day(2026, 10, 9));
        Put(Path.Combine(dir, "Maps", "Дом-client.db"), 7_340_032, Day(2026, 6, 14));

        var client = new GameProfile
        {
            Id = ClientFolder,
            Name = Loc.T("profile.defaultClientName"),
            Kind = ProfileKind.Client,
            GameDir = stand.GameDir,
            DataDir = dir,
        };
        AppWindows.Current.ShowCleanup(client);
    }

    /// <summary>Клиентский профиль с пустой папкой данных: «Убирать нечего», кнопка выключена.</summary>
    [SnapshotDialog("cleanup.empty")]
    private static void Empty(MainViewModel main)
    {
        var stand = Stand(main);
        var dir = Path.Combine(StandRoot(stand), ClientFolder + "-empty");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        AppWindows.Current.ShowCleanup(new GameProfile
        {
            Id = ClientFolder + "-empty",
            Name = Loc.T("profile.defaultClientName"),
            Kind = ProfileKind.Client,
            GameDir = stand.GameDir,
            DataDir = dir,
        });
    }

    private static GameProfile Stand(MainViewModel main) =>
        main.ActiveProfile?.Model ?? throw new InvalidOperationException("Снимок окна уборки: нет профиля стенда");

    /// <summary>
    /// Временная папка стенда (в ней vsdata и сборки окон с их папками data). Проверяем, что это она, а не настоящие
    /// данные: папка данных программы (data рядом с exe) — двумя уровнями ниже той же папки, что и vsdata.
    /// </summary>
    private static string StandRoot(GameProfile stand)
    {
        static string Norm(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
        var root = string.IsNullOrWhiteSpace(stand.DataDir) ? null : Path.GetDirectoryName(Norm(stand.DataDir));
        var appRoot = Path.GetDirectoryName(Path.GetDirectoryName(Norm(AppPaths.Root)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (root is null || appRoot is null || !AppPaths.IsPortable || !string.Equals(root, appRoot, comparison))
            throw new InvalidOperationException("Снимок окна уборки снимается только на стенде build/snapshot.ps1");
        return root;
    }

    private static DateTime Day(int year, int month, int day) => new(year, month, day, 18, 0, 0, DateTimeKind.Utc);

    /// <summary>Пустой файл нужной длины (место на диске не заполняется) с датой изменения.</summary>
    private static void Put(string path, long size, DateTime changedUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write)) stream.SetLength(size);
        File.SetLastWriteTimeUtc(path, changedUtc);
    }
}

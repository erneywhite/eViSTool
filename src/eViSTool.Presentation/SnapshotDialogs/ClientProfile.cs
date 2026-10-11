using eViSTool.Core;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Окно нового клиентского профиля: «+ Клиент» на основе клиентского профиля (dialog.clientprofile), его клон
/// (dialog.clientprofile.clone), пустое название — ошибка под сводкой (dialog.clientprofile.error) и основа без папки
/// данных — модов нет, остаётся только «с нуля» (dialog.clientprofile.nomods).
/// Профиль стенда серверный, поэтому основу собираем сами: папка данных клиента — рядом с vsdata во временной папке
/// стенда (build/snapshot.ps1), с настройками игры, двумя модами, настройкой мода и миром. Окно только открывается:
/// ничего не копируется.
/// </summary>
internal static class ClientProfile
{
    private const string Folder = "snapshot-client";

    [SnapshotDialog("clientprofile")]
    private static void New(MainViewModel main) =>
        AppWindows.Current.ClientProfile(Source(main), clone: false, Loc.T("profile.newClient", main.Profiles.Count + 1));

    /// <summary>Клон: миры по умолчанию копируются (галочка появится, когда посчитается их размер).</summary>
    [SnapshotDialog("clientprofile.clone")]
    private static void Clone(MainViewModel main)
    {
        var source = Source(main);
        AppWindows.Current.ClientProfile(source, clone: true, Loc.T("clone.copySuffix", source.Name));
    }

    /// <summary>Название пустое: папка не подставляется, под сводкой — почему нельзя, кнопка выключена.</summary>
    [SnapshotDialog("clientprofile.error")]
    private static void Error(MainViewModel main) => AppWindows.Current.ClientProfile(Source(main), clone: false, "");

    /// <summary>Игра с основой ещё не запускалась (папки данных нет): «Общие моды» и «Своя копия» выключены.</summary>
    [SnapshotDialog("clientprofile.nomods")]
    private static void NoMods(MainViewModel main)
    {
        var stand = Stand(main);
        var source = new GameProfile
        {
            Name = Loc.T("profile.defaultClientName"),
            Kind = ProfileKind.Client,
            GameDir = stand.GameDir,
            DataDir = Path.Combine(StandRoot(stand), Folder + "-missing"), // не создаётся
        };
        AppWindows.Current.ClientProfile(source, clone: false, Loc.T("profile.newClient", main.Profiles.Count + 1));
    }

    private static GameProfile Stand(MainViewModel main) =>
        main.ActiveProfile?.Model ?? throw new InvalidOperationException("Снимок окна клиентского профиля: нет профиля стенда");

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
            throw new InvalidOperationException("Снимок окна клиентского профиля снимается только на стенде build/snapshot.ps1");
        return root;
    }

    /// <summary>Клиентский профиль «Клиент» на свежей папке данных рядом с vsdata: прошлый снимок мог оставить свою.</summary>
    private static GameProfile Source(MainViewModel main)
    {
        var stand = Stand(main);
        var dir = Path.Combine(StandRoot(stand), Folder);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        var mods = Directory.CreateDirectory(Path.Combine(dir, "Mods")).FullName;
        foreach (var mod in new[] { "carryon_1.8.0.zip", "primitivesurvival_3.7.6.zip" })
            File.Copy(Path.Combine(stand.DataDir!, "Mods", mod), Path.Combine(mods, mod));
        Directory.CreateDirectory(Path.Combine(dir, "ModConfig"));
        File.Copy(Path.Combine(stand.DataDir!, "ModConfig", "carryon.json"), Path.Combine(dir, "ModConfig", "carryon.json"));

        // настройки игры: моды — из встроенной папки игры и своей папки Mods
        var settings = new Newtonsoft.Json.Linq.JObject
        {
            ["stringListSettings"] = new Newtonsoft.Json.Linq.JObject
            {
                ["modPaths"] = new Newtonsoft.Json.Linq.JArray("Mods", mods),
                ["disabledMods"] = new Newtonsoft.Json.Linq.JArray(),
            },
            ["intSettings"] = new Newtonsoft.Json.Linq.JObject { ["viewDistance"] = 256 },
        };
        File.WriteAllText(Path.Combine(dir, ClientProfileCloner.DefaultSettingsName), settings.ToString());

        // мир одиночной игры и карта: размер виден в галочке «Скопировать миры…»
        Directory.CreateDirectory(Path.Combine(dir, "Saves"));
        File.WriteAllBytes(Path.Combine(dir, "Saves", "Дом.vcdbs"), new byte[2_516_582]);
        Directory.CreateDirectory(Path.Combine(dir, "Maps"));
        File.WriteAllBytes(Path.Combine(dir, "Maps", "Дом-client.db"), new byte[184_320]);

        return new GameProfile { Name = Loc.T("profile.defaultClientName"), Kind = ProfileKind.Client, GameDir = stand.GameDir, DataDir = dir };
    }
}

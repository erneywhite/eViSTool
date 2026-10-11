using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Выбор папки на машине с сервером: dialog.remotefolder (открыта папка рядом с данными стенда, в ней несколько папок),
/// dialog.remotefolder.notfound (такого пути там нет — показана ближайшая папка выше и красная строка) и
/// dialog.remotefolder.offline (нет связи с агентом — пустой список и красная строка, сети тесту не нужно). Папки читаются
/// только на папках стенда (временная копия tests/snapshots/fixture), ничего не записывается.
/// </summary>
internal static class RemoteFolder
{
    /// <summary>Открыта папка, где лежат данные стенда: рядом с ними — игра стенда и сборки окон.</summary>
    [SnapshotDialog("remotefolder")]
    private static void Open(MainViewModel main)
    {
        var dataDir = DataDir(main);
        AppWindows.Current.PickRemoteFolder(Local(main), Path.GetDirectoryName(dataDir), Loc.T("backupdir.pick"));
    }

    /// <summary>Папки для копий в данных стенда нет — окно показывает ближайшую существующую выше.</summary>
    [SnapshotDialog("remotefolder.notfound")]
    private static void NotFound(MainViewModel main) =>
        AppWindows.Current.PickRemoteFolder(Local(main), Path.Combine(DataDir(main), "Backups", "weekly"), Loc.T("backupdir.pick"));

    /// <summary>Агента нет на связи: сервер на другом компьютере, а клиента для него у стенда нет.</summary>
    [SnapshotDialog("remotefolder.offline")]
    private static void Offline(MainViewModel main) =>
        AppWindows.Current.PickRemoteFolder(new RemoteServerData(() => null), "/mnt/backups", Loc.T("backupdir.pick"));

    private static string DataDir(MainViewModel main) =>
        main.ActiveProfile?.Model.DataDir ?? throw new InvalidOperationException("У стенда нет папки данных");

    /// <summary>Сервер стенда как на этой машине: папки читаются напрямую, как у профиля без удалённого агента.</summary>
    private static LocalServerData Local(MainViewModel main)
    {
        var profile = main.ActiveProfile?.Model ?? throw new InvalidOperationException("У стенда нет активного профиля");
        return new LocalServerData(new ServerFiles(profile.Id, DataDir(main), BackupStore.Slug(profile.Name)));
    }
}

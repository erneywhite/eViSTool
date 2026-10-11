using eViSTool.Core.Localization;
using eViSTool.Core.Server.Remote;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Окно кода подключения к серверу на другом компьютере: dialog.connectcode, .bad, .good. Коды выдуманные (адрес из
/// TEST-NET, ключ и отпечаток — не настоящие); проверку связи снимки не запускают, сеть не нужна.
/// Окно на WPF модели не берёт и с кодом в поле не откроется: состояния .bad и .good у него — пустое поле.
/// </summary>
internal static class ConnectCode
{
    /// <summary>Только что открытое окно: поле пустое, «Проверить подключение» и «Подключить» выключены.</summary>
    [SnapshotDialog("connectcode")]
    private static void Open(MainViewModel main) => Show(null);

    /// <summary>В поле не код (обрезан при копировании): под полем — ошибка разбора.</summary>
    [SnapshotDialog("connectcode.bad")]
    private static void OpenBad(MainViewModel main) => Show("evis1.eyJoIjoiMTkyLjE2OC4xLjQyIiwicCI6NDI0MjAsImsiOiJReDc");

    /// <summary>В поле код: под полем — куда он ведёт, кнопки включены.</summary>
    [SnapshotDialog("connectcode.good")]
    private static void OpenGood(MainViewModel main) =>
        Show(new ConnectionCode("192.0.2.42", 42420, "Qx7-made-up-key-for-snapshot", new string('c', 64)).ToString());

    private static void Show(string? text)
    {
        ConnectCodeViewModel.SnapshotText = text;
        AppWindows.Current.AskConnectionCode(Loc.T("newprofile.connectOk"));
    }
}

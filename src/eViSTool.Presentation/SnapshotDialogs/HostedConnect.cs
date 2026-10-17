using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Versioning;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Окно «Сервер у хостера» (подключение по FTP): dialog.hostedconnect, .ok, .error, .cert, .edit, .sftp. Адреса из
/// TEST-NET-3 (203.0.113.x), логин, пароль и отпечаток выдуманные; проверку связи снимки не запускают, сеть не нужна —
/// итог проверки модель получает готовым.
/// </summary>
internal static class HostedConnect
{
    private const string Host = "203.0.113.40";

    /// <summary>Только что открытое окно: всё пусто, «Проверить подключение» и «Подключить» выключены.</summary>
    [SnapshotDialog("hostedconnect")]
    private static void Open(MainViewModel main) => Show(null, null);

    /// <summary>Поля заполнены, проверка прошла: итог одной строкой зелёным.</summary>
    [SnapshotDialog("hostedconnect.ok")]
    private static void OpenOk(MainViewModel main) => Show(null, vm =>
    {
        Fill(vm);
        var layout = new HostedLayout("/", "/serverconfig.json", "/", "/", ["/Mods", "/gportal_mods"], "/Mods", []);
        var result = new HostedCheckResult(layout, ModVersion.ParseOrNull("1.22.7"), GameVersionSource.VersionFile, 12,
            new RemoteFeatures(true, true, false, null));
        vm.CheckText = HostedConnectViewModel.CheckSummary(result);
        vm.CheckTone = RowTone.Good;
    });

    /// <summary>Сервер не принял логин или пароль: ошибка словами красным.</summary>
    [SnapshotDialog("hostedconnect.error")]
    private static void OpenError(MainViewModel main) => Show(null, vm =>
    {
        Fill(vm);
        vm.CheckText = HostedSecret.Describe(new RemoteFilesException(RemoteFailure.Auth, "530 Login incorrect"));
        vm.CheckTone = RowTone.Danger;
    });

    /// <summary>Сертификат сервера не подтверждён: отпечаток и кнопка «Доверять этому сертификату».</summary>
    [SnapshotDialog("hostedconnect.cert")]
    private static void OpenCert(MainViewModel main) => Show(null, vm =>
    {
        Fill(vm);
        vm.TlsIndex = 1; // только с шифрованием
        vm.ShowCertificatePrompt("4F:A3:9C:21:0B:77:D5:E8:13:6A:C0:5E:92:BB:48:1D:F6:0A:73:2C:85:E1:39:7D:AA:64:10:CF:58:9B:E2:07");
    });

    /// <summary>Правка готового подключения: пароль сохранён (подсказка в поле), «Дополнительно» раскрыто.</summary>
    [SnapshotDialog("hostedconnect.edit")]
    private static void OpenEdit(MainViewModel main) => Show(new HostedConnection
    {
        Host = Host, Port = 58021, Tls = FtpTls.None, User = "gp_user", Password = HostedSecret.Protect("made-up-password"),
        Root = "/g123456", ActiveMode = true,
    }, null);

    /// <summary>В адресе ссылка sftp://: объяснение, что SFTP пока нет, и «Подключить» выключена.</summary>
    [SnapshotDialog("hostedconnect.sftp")]
    private static void OpenSftp(MainViewModel main) => Show(null, vm =>
    {
        vm.Address = "sftp://" + Host + ":2022";
        vm.User = "gp_user";
    });

    private static void Fill(HostedConnectViewModel vm)
    {
        vm.Address = Host;
        vm.PortText = "58021";
        vm.User = "gp_user";
        vm.Password = "made-up-password";
    }

    private static void Show(HostedConnection? existing, Action<HostedConnectViewModel>? init)
    {
        HostedConnectViewModel.SnapshotInit = init;
        AppWindows.Current.AskHostedConnection(existing, existing is null ? Loc.T("newprofile.connectOk") : Loc.T("settings.hostedEditOk"));
    }
}

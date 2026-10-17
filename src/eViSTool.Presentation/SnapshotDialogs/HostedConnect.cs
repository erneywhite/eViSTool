using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Versioning;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Окно «Сервер у хостера» (подключение по FTP или SFTP): dialog.hostedconnect, .ok, .error, .cert, .edit, .sftp,
/// .sftpkey, .hostkey, .hostkeychanged. Адреса из TEST-NET-3 (203.0.113.x), логин, пароль, путь к ключу и отпечатки
/// выдуманные; проверку связи снимки не запускают, сеть не нужна — итог проверки модель получает готовым.
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

    /// <summary>SFTP по паролю: вместо шифрования — список «Вход», проверка прошла.</summary>
    [SnapshotDialog("hostedconnect.sftp")]
    private static void OpenSftp(MainViewModel main) => Show(null, vm =>
    {
        vm.Address = "sftp://" + Host + ":2022";
        vm.User = "gp_user";
        vm.Password = "made-up-password";
        vm.CommitAddress();
        var layout = new HostedLayout("/", "/serverconfig.json", "/", "/", ["/Mods"], "/Mods", []);
        var result = new HostedCheckResult(layout, ModVersion.ParseOrNull("1.22.7"), GameVersionSource.VersionFile, 8,
            new RemoteFeatures(true, true, true, "SSH-2.0-OpenSSH_9.6"));
        vm.CheckText = HostedConnectViewModel.CheckSummary(result);
        vm.CheckTone = RowTone.Good;
    });

    /// <summary>SFTP по файлу ключа: путь к ключу, «Обзор…» и необязательный пароль ключа.</summary>
    [SnapshotDialog("hostedconnect.sftpkey")]
    private static void OpenSftpKey(MainViewModel main) => Show(null, vm =>
    {
        FillSftpKey(vm);
        // файла ключа на машине снимка нет: подсказка — обычная, как при найденном файле
        vm.KeyFileHint = Loc.T("hosted.keyHint");
        vm.KeyFileTone = RowTone.Muted;
    });

    /// <summary>Ключ сервера SFTP виден впервые: отпечаток и кнопка «Доверять этому ключу».</summary>
    [SnapshotDialog("hostedconnect.hostkey")]
    private static void OpenHostKey(MainViewModel main) => Show(null, vm =>
    {
        FillSftpKey(vm);
        vm.KeyFileHint = Loc.T("hosted.keyHint");
        vm.KeyFileTone = RowTone.Muted;
        vm.ShowHostKeyPrompt(HostKey, changed: false);
    });

    /// <summary>Ключ сервера SFTP сменился с прошлого раза: предупреждение и «Доверять новому ключу».</summary>
    [SnapshotDialog("hostedconnect.hostkeychanged")]
    private static void OpenHostKeyChanged(MainViewModel main) => Show(null, vm =>
    {
        vm.Address = "sftp://" + Host + ":2022";
        vm.User = "gp_user";
        vm.Password = "made-up-password";
        vm.CommitAddress();
        vm.ShowHostKeyPrompt(HostKey, changed: true);
    });

    private const string HostKey = "SHA256:q3Yp0bX7cN2vTzK8wR5mLs1JdHf4GuAe9iOkPnWy6xE";

    private static void FillSftpKey(HostedConnectViewModel vm)
    {
        vm.Address = "sftp://" + Host + ":2022";
        vm.User = "gp_user";
        vm.CommitAddress();
        vm.AuthIndex = 1;
        vm.KeyFile = "~/.ssh/id_ed25519";
    }

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

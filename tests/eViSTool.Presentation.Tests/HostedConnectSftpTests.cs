using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Versioning;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Окно «Сервер у хостера» для SFTP: протокол по ссылке sftp://, порт 22 по умолчанию, вместо шифрования — способ входа
/// (пароль или файл ключа с необязательным паролем ключа), доверие к ключу сервера, как к сертификату FTPS, и отказ,
/// когда ключ сменился. Проверка подключения подменена делегатом — сети тесты не трогают. Адреса из TEST-NET-3,
/// пароли и отпечатки выдуманные.
/// </summary>
public sealed class HostedConnectSftpTests : IDisposable
{
    private const string Host = "203.0.113.40";
    private static readonly string HostKey = "SHA256:" + new string('K', 43);
    private static readonly string OtherKey = "SHA256:" + new string('Z', 43);

    private readonly string _dir = Directory.CreateTempSubdirectory("evis-sftp-vm-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* временная папка */ }
    }

    private delegate Task<HostedCheckResult> Check(HostedEndpoint endpoint, HostedConnection settings, CancellationToken ct);

    private static HostedConnectViewModel Model(Check? check = null, HostedConnection? existing = null, Func<string?>? pick = null) =>
        new(existing, "Подключить", check is null
            ? (_, _, _) => throw new Xunit.Sdk.XunitException("проверка подключения в этом тесте не ожидалась")
            : new Func<HostedEndpoint, HostedConnection, CancellationToken, Task<HostedCheckResult>>(check))
        {
            PickKeyFile = pick ?? (() => throw new Xunit.Sdk.XunitException("выбор файла в этом тесте не ожидался")),
        };

    private static HostedCheckResult Found() =>
        new(new HostedLayout("/", "/serverconfig.json", "/", "/", ["/Mods"], "/Mods", []), ModVersion.ParseOrNull("1.22.7"),
            GameVersionSource.VersionFile, 3, new RemoteFeatures(true, true, true, "SSH-2.0-OpenSSH_9.6"));

    private string KeyFile(string name = "id_ed25519")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "not a real key");
        return path;
    }

    private HostedConnectViewModel SftpWithKey(Check? check = null)
    {
        var vm = Model(check);
        vm.Address = "sftp://gp_user@" + Host + ":2022";
        vm.AuthIndex = 1;
        vm.KeyFile = KeyFile();
        return vm;
    }

    // ===== переключатель «Протокол» =====

    [Fact]
    public void ProtocolSwitch_MovesTheDefaultPort_BackAndForth()
    {
        var vm = Model();
        vm.Address = Host;
        Assert.True(vm.IsFtp);
        Assert.Equal("21", vm.PortText);

        vm.IsSftp = true;
        Assert.Equal("22", vm.PortText);
        Assert.True(vm.IsSftp);
        Assert.False(vm.IsFtp);

        vm.IsFtp = true;
        Assert.Equal("21", vm.PortText);

        vm.PortText = "";
        vm.IsSftp = true; // пустой порт — тоже «по умолчанию»
        Assert.Equal("22", vm.PortText);
    }

    [Fact]
    public void ProtocolSwitch_KeepsAPortTheUserTyped()
    {
        var vm = Model();
        vm.Address = Host;
        vm.PortText = "58021";
        vm.IsSftp = true;
        Assert.Equal("58021", vm.PortText);
        vm.IsFtp = true;
        Assert.Equal("58021", vm.PortText);

        vm.PortText = "2022";
        vm.IsSftp = true;
        Assert.Equal("2022", vm.PortText);
    }

    [Fact]
    public void ProtocolSwitch_WithoutScheme_IsSftp_AndSurvivesAddressEdits()
    {
        var vm = Model();
        vm.IsSftp = true;
        vm.Address = Host; // схемы нет, но выбран SFTP
        Assert.True(vm.IsSftp);
        Assert.Equal("22", vm.PortText);
        Assert.False(vm.IsFtp);
        vm.Address = Host + "x";
        Assert.True(vm.IsSftp);

        var result = vm.Accept()!;
        Assert.Equal(HostedProtocol.Sftp, result.Protocol);
        Assert.Equal(22, result.Port);
    }

    [Fact]
    public void Scheme_SetsTheSwitch_AndSwitchRewritesTheScheme()
    {
        var vm = Model();
        vm.Address = "sftp://u@" + Host + ":2022/srv";
        Assert.True(vm.IsSftp);
        Assert.Equal("2022", vm.PortText);

        vm.IsFtp = true; // sftp:// из поля уходит, порт 2022 не трогаем: он вписан в ссылке
        Assert.DoesNotContain("sftp://", vm.Address);
        Assert.True(vm.IsFtp);
        Assert.Equal("2022", vm.PortText);

        vm.IsSftp = true; // схемы в поле нет: SFTP живёт в переключателе, а схему поле получает, когда из него ушли
        Assert.True(vm.IsSftp);
        vm.CommitAddress();
        Assert.StartsWith("sftp://", vm.Address);
        vm.IsFtp = true;
        vm.IsSftp = true; // и ещё раз со схемой в поле
        Assert.True(vm.IsSftp);
        vm.Address = "ftp://" + Host;
        Assert.True(vm.IsFtp);
    }

    [Fact]
    public void ProtocolSwitch_ResetsTheCheckAndTheSavedLayout()
    {
        var vm = Model((_, _, _) => Task.FromResult(Found()));
        vm.Address = Host;
        vm.CheckCommand.Execute(null);
        Assert.NotEqual("", vm.CheckText);

        vm.IsSftp = true;
        Assert.Equal("", vm.CheckText);
        Assert.Null(vm.Accept()!.ConfigPath);
    }

    [Fact]
    public void SavedSftpProfile_OpensWithTheSwitchOnSftp_AndItsOwnPort()
    {
        var vm = Model(existing: new HostedConnection { Protocol = HostedProtocol.Sftp, Host = Host, Port = 2022, User = "gp_user" });
        Assert.True(vm.IsSftp);
        Assert.Equal("2022", vm.PortText);
        Assert.Equal(HostedProtocol.Sftp, vm.Accept()!.Protocol);

        vm.IsFtp = true;
        Assert.Equal("2022", vm.PortText); // своё значение не подменяем
        Assert.Equal(HostedProtocol.Ftp, vm.Accept()!.Protocol);
    }

    // ===== адрес, порт, поля FTP =====

    [Fact]
    public void SftpLinkWithoutPort_Means22_AndDroppingTheSchemeGivesThePortBack()
    {
        var vm = Model();
        vm.PortText = "58021";
        vm.Address = "sftp://" + Host;
        Assert.Equal("22", vm.PortText);
        Assert.Equal(Loc.T("hosted.portHintSftp"), vm.PortHint);
        Assert.Equal(RowTone.Muted, vm.PortTone);

        vm.Address = Host; // передумали: обычный FTP
        Assert.False(vm.IsSftp);
        Assert.Equal("58021", vm.PortText);
    }

    [Fact]
    public void EditingTheAddress_KeepsTheSavedSftpPort()
    {
        var existing = new HostedConnection { Protocol = HostedProtocol.Sftp, Host = Host, Port = 2022, User = "gp_user" };
        var vm = Model(existing: existing);

        vm.Address = "sftp://203.0.113.41"; // поправили хост, схема осталась в поле
        Assert.Equal("2022", vm.PortText);

        // адрес набрали заново по буквам: по дороге он бывает и обычным хостом FTP («sftp», «sftp:»)
        const string typed = "sftp://panel.example.net";
        for (var i = 1; i <= typed.Length; i++) vm.Address = typed[..i];
        Assert.True(vm.IsSftp);
        Assert.Equal("2022", vm.PortText);
        Assert.Equal(2022, vm.Accept()!.Port);
    }

    [Fact]
    public void PortTypedForSftp_SurvivesAddressEdits()
    {
        var vm = Model();
        vm.Address = "sftp://panel.example.net";
        vm.CommitAddress();
        vm.PortText = "2022"; // порт из панели хостера вписали руками

        vm.Address = "sftp://panel.example.com";

        Assert.Equal("2022", vm.PortText);
    }

    [Theory]
    [InlineData("22")]
    [InlineData("2022")]
    [InlineData("8822")]
    public void SftpLookingPortTypedForFtp_StaysWhenTheSchemeIsAdded(string port)
    {
        var vm = Model();
        vm.Address = Host;
        vm.PortText = port; // подсказка под портом советует дописать sftp:// — так и сделали
        Assert.Equal(Loc.T("hosted.portSftp", int.Parse(port)), vm.PortHint);

        vm.Address = "sftp://" + Host;

        Assert.Equal(port, vm.PortText);
    }

    [Fact]
    public void PortRemovedFromTheLink_FallsBackToTheSftpPortItHadBefore()
    {
        // новый профиль: до ссылки в поле 21 от FTP — без порта в ссылке у SFTP встаёт 22
        var fresh = Model();
        fresh.Address = "sftp://" + Host + ":2200";
        Assert.Equal("2200", fresh.PortText);
        fresh.Address = "sftp://" + Host;
        Assert.Equal("22", fresh.PortText);

        // профиль SFTP с портом 2022: убрали порт из ссылки — вернулся 2022, а не 22
        var saved = Model(existing: new HostedConnection { Protocol = HostedProtocol.Sftp, Host = Host, Port = 2022, User = "gp_user" });
        saved.Address = "sftp://" + Host + ":2200";
        Assert.Equal("2200", saved.PortText);
        saved.Address = "sftp://" + Host;
        Assert.Equal("2022", saved.PortText);
    }

    [Theory]
    [InlineData("21")]
    [InlineData("990")]
    [InlineData("8821")]
    public void FtpPorts_WithSftp_AreWarnings(string port)
    {
        var vm = Model();
        vm.Address = "sftp://" + Host;
        vm.PortText = port;
        Assert.Equal(Loc.T("hosted.portFtpNotSftp", int.Parse(port)), vm.PortHint);
        Assert.Equal(RowTone.Warn, vm.PortTone);
    }

    [Fact]
    public void RootHint_ForSftp_TalksAboutTheServerFolder()
    {
        var vm = Model();
        vm.Address = "sftp://" + Host;
        Assert.Equal(Loc.T("hosted.rootHintSftp"), vm.RootHint);
        vm.Address = Host;
        Assert.Equal(Loc.T("hosted.rootHint"), vm.RootHint);
    }

    [Fact]
    public void PasswordLogin_Accept_SavesSftp_WithoutFtpOnlyFields()
    {
        var vm = Model();
        vm.ActiveMode = true; // включили для FTP, потом перешли на SFTP — у SFTP его нет
        vm.Address = "sftp://gp_user:made-up-password@" + Host + ":2022/srv";

        var result = vm.Accept()!;

        Assert.Equal(HostedProtocol.Sftp, result.Protocol);
        Assert.Equal(Host, result.Host);
        Assert.Equal(2022, result.Port);
        Assert.Equal("gp_user", result.User);
        Assert.Equal("/srv", result.Root);
        Assert.False(result.ActiveMode);
        Assert.Null(result.KeyFile);
        Assert.Null(result.KeyPassphrase);
        Assert.Equal("made-up-password", HostedSecret.Unprotect(result.Password));
        Assert.DoesNotContain("made-up-password", result.ToString());
        Assert.Equal("sftp://" + Host, vm.Address); // логин и пароль из видимого поля ушли
    }

    [Fact]
    public async Task PasswordLogin_Check_SendsAnSftpEndpoint_AndLeavesTlsAlone()
    {
        HostedEndpoint? endpoint = null;
        var vm = Model((e, _, _) => { endpoint = e; return Task.FromResult(Found()); });
        vm.Address = "sftp://" + Host + ":2022";
        vm.User = "gp_user";
        vm.Password = "made-up-password";
        vm.ActiveMode = true;

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(HostedProtocol.Sftp, endpoint!.Protocol);
        Assert.Equal(2022, endpoint.Port);
        Assert.Equal("made-up-password", endpoint.Password);
        Assert.Null(endpoint.KeyFile);
        Assert.False(endpoint.ActiveMode);
        Assert.DoesNotContain("made-up-password", endpoint.ToString());
        Assert.Equal(RowTone.Good, vm.CheckTone);
        Assert.Contains(Loc.T("hosted.encrypted"), vm.CheckText);
        Assert.Equal(FtpTls.Auto, vm.Tls); // у SFTP шифрование всегда — список TLS не переключается
        Assert.Equal("serverconfig.json", vm.Accept()!.ConfigPath);
    }

    // ===== вход по ключу =====

    [Fact]
    public void KeyLogin_NeedsAKeyFile_AndHidesThePasswordField()
    {
        var vm = Model();
        vm.Address = "sftp://" + Host;
        Assert.False(vm.IsKeyLogin);
        vm.AuthIndex = 1;

        Assert.True(vm.IsKeyLogin);
        Assert.False(vm.CanAccept); // без файла ключа войти нечем
        Assert.Null(vm.Accept());
        vm.KeyFile = "  ";
        Assert.False(vm.CanAccept);
        vm.KeyFile = KeyFile();
        Assert.True(vm.CanAccept);
        Assert.Equal(Loc.T("hosted.keyHint"), vm.KeyFileHint);
        Assert.Equal(RowTone.Muted, vm.KeyFileTone);
        Assert.Equal(Loc.T("hosted.keyPassphraseHint"), vm.KeyPassphraseNote);

        vm.Address = Host; // FTP: входа по ключу у него нет
        Assert.False(vm.IsKeyLogin);
        Assert.True(vm.CanAccept);
    }

    [Fact]
    public void KeyLogin_Accept_SavesTheKeyAndEncryptedPassphrase_ButNoPassword()
    {
        var vm = SftpWithKey();
        vm.Password = "typed-before-switching"; // набрали пароль, потом выбрали вход по ключу
        vm.KeyPassphrase = "made-up-passphrase";

        var result = vm.Accept()!;

        Assert.Equal(HostedProtocol.Sftp, result.Protocol);
        Assert.Equal(Path.Combine(_dir, "id_ed25519"), result.KeyFile);
        Assert.NotEqual("made-up-passphrase", result.KeyPassphrase);
        Assert.Equal("made-up-passphrase", HostedSecret.Unprotect(result.KeyPassphrase));
        Assert.Null(result.Password);
        Assert.DoesNotContain("made-up-passphrase", result.ToString());
    }

    [Fact]
    public async Task KeyLogin_Check_SendsTheKeyAndPassphrase()
    {
        HostedEndpoint? endpoint = null;
        var vm = SftpWithKey((e, _, _) => { endpoint = e; return Task.FromResult(Found()); });
        vm.KeyPassphrase = "made-up-passphrase";

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(Path.Combine(_dir, "id_ed25519"), endpoint!.KeyFile);
        Assert.Equal("made-up-passphrase", endpoint.KeyPassphrase);
        Assert.Equal("", endpoint.Password);
        Assert.Equal("gp_user", endpoint.User);
        Assert.DoesNotContain("made-up-passphrase", endpoint.ToString());
        Assert.DoesNotContain("made-up-passphrase", vm.CheckText);
        Assert.Equal(RowTone.Good, vm.CheckTone);
    }

    [Fact]
    public async Task KeyLogin_WithoutPassphrase_SendsNone()
    {
        HostedEndpoint? endpoint = null;
        var vm = SftpWithKey((e, _, _) => { endpoint = e; return Task.FromResult(Found()); });
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Null(endpoint!.KeyPassphrase);
        Assert.Null(vm.Accept()!.KeyPassphrase);
    }

    [Fact]
    public async Task KeyLogin_MissingFile_IsRedRightAway_AndTheCheckSaysSoWithoutGoingOnline()
    {
        var calls = 0;
        var vm = Model((_, _, _) => { calls++; return Task.FromResult(Found()); });
        vm.Address = "sftp://gp_user@" + Host;
        vm.AuthIndex = 1;
        var missing = Path.Combine(_dir, "no_such_key");
        vm.KeyFile = missing;

        Assert.Equal(Loc.T("hosted.keyMissing", missing), vm.KeyFileHint);
        Assert.Equal(RowTone.Danger, vm.KeyFileTone);
        Assert.True(vm.CanAccept); // сохранить можно — ключ мог быть на отключённом диске

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(0, calls);
        Assert.Equal(Loc.T("hosted.keyMissing", missing), vm.CheckText);
        Assert.Equal(RowTone.Danger, vm.CheckTone);
        Assert.False(vm.IsChecking);
    }

    [Fact]
    public async Task KeyLogin_OnlyTheKeyFieldGoesToTheDisk()
    {
        // ключ может лежать на сетевой папке, которая отвечает долго: буквы в других полях её не ждут
        var probes = new List<string>();
        var onDisk = false;
        var vm = new HostedConnectViewModel(null, "Подключить", (_, _, _) => Task.FromResult(Found()))
        {
            KeyFileProbe = path => { probes.Add(path); return onDisk; },
        };
        vm.Address = "sftp://" + Host;
        vm.AuthIndex = 1;
        const string share = @"\\nas\keys\id_ed25519";
        vm.KeyFile = share;
        Assert.Equal(new[] { share }, probes);
        Assert.Equal(Loc.T("hosted.keyMissing", share), vm.KeyFileHint);

        vm.User = "gp_user";
        vm.Password = "made-up-password";
        vm.PortText = "2022";
        vm.Root = "/srv";
        vm.KeyPassphrase = "made-up-passphrase";
        vm.Address = "sftp://203.0.113.41";
        Assert.Single(probes);
        Assert.Equal(RowTone.Danger, vm.KeyFileTone); // подсказка та же — путь не менялся

        // ключ положили на место; «Проверить» смотрит на диск заново — и подсказка тоже
        onDisk = true;
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(2, probes.Count);
        Assert.Equal(Loc.T("hosted.keyHint"), vm.KeyFileHint);
        Assert.Equal(RowTone.Good, vm.CheckTone);

        // вход по паролю и снова по ключу — тоже свежий взгляд
        vm.AuthIndex = 0;
        vm.AuthIndex = 1;
        Assert.Equal(3, probes.Count);
    }

    [Fact]
    public async Task KeyLogin_WrongPassphrase_IsSaidInWords()
    {
        var vm = SftpWithKey((_, _, _) => Task.FromException<HostedCheckResult>(
            new RemoteFilesException(RemoteFailure.KeyPassphrase, "id_ed25519: wrong passphrase")));
        vm.KeyPassphrase = "made-up-passphrase";

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(Loc.T("hosted.errKeyPassphrase"), vm.CheckText);
        Assert.Equal(RowTone.Danger, vm.CheckTone);
    }

    [Fact]
    public void Browse_PutsThePickedFileIntoTheField_AndCancelKeepsIt()
    {
        string? next = Path.Combine(_dir, "picked_key");
        var vm = Model(pick: () => next);
        vm.Address = "sftp://" + Host;
        vm.AuthIndex = 1;

        vm.BrowseKeyCommand.Execute(null);
        Assert.Equal(Path.Combine(_dir, "picked_key"), vm.KeyFile);

        next = null; // передумали в окне выбора
        vm.BrowseKeyCommand.Execute(null);
        Assert.Equal(Path.Combine(_dir, "picked_key"), vm.KeyFile);
    }

    // ===== ключ сервера =====

    [Fact]
    public async Task UnknownHostKey_ShowsTheFingerprint_AndTrustRechecksWithIt()
    {
        var endpoints = new List<HostedEndpoint>();
        var vm = SftpWithKey((e, _, _) =>
        {
            endpoints.Add(e);
            return e.TrustedHostKey == HostKey
                ? Task.FromResult(Found())
                : Task.FromException<HostedCheckResult>(new RemoteFilesException(RemoteFailure.CertificateUntrusted, "untrusted host key")
                {
                    CertificateSha256 = HostKey, CertificateSubject = "ssh-ed25519 256", IsHostKey = true,
                });
        });

        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingCertificate);
        Assert.Equal(Loc.T("hosted.trustHostKey", HostKey), vm.CertificateText);
        Assert.Equal(Loc.T("hosted.trustKey"), vm.TrustText);
        Assert.Null(endpoints[0].TrustedHostKey);

        await vm.TrustCertificateCommand.ExecuteAsync(null);

        Assert.Equal(2, endpoints.Count);
        Assert.Equal(HostKey, endpoints[1].TrustedHostKey);
        Assert.Null(endpoints[1].TrustedCertificateSha256); // ключ SSH — не сертификат TLS
        Assert.False(vm.HasPendingCertificate);
        Assert.Equal(RowTone.Good, vm.CheckTone);
        var result = vm.Accept()!;
        Assert.Equal(HostKey, result.HostKeySha256);
        Assert.Null(result.CertificateSha256);
    }

    [Fact]
    public async Task ChangedHostKey_IsARefusal_WithItsOwnWords_UntilThePersonTrustsTheNewKey()
    {
        var existing = new HostedConnection
        {
            Protocol = HostedProtocol.Sftp, Host = Host, Port = 2022, User = "gp_user", Password = HostedSecret.Protect("saved-password"),
            HostKeySha256 = HostKey,
        };
        var endpoints = new List<HostedEndpoint>();
        var vm = Model((e, _, _) =>
        {
            endpoints.Add(e);
            return e.TrustedHostKey == OtherKey
                ? Task.FromResult(Found())
                : Task.FromException<HostedCheckResult>(new RemoteFilesException(RemoteFailure.CertificateUntrusted, "untrusted host key")
                {
                    CertificateSha256 = OtherKey, IsHostKey = true, HostKeyChanged = true,
                });
        }, existing);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(HostKey, endpoints[0].TrustedHostKey); // проверка шла с прежним доверенным ключом
        Assert.True(vm.HasPendingCertificate);
        Assert.Equal(Loc.T("hosted.hostKeyChanged", OtherKey), vm.CertificateText);
        Assert.Equal(Loc.T("hosted.trustNewKey"), vm.TrustText);
        Assert.Equal(HostKey, vm.Accept()!.HostKeySha256); // «ОК» без доверия новому ключу — прежний остаётся

        await vm.TrustCertificateCommand.ExecuteAsync(null);
        Assert.Equal(OtherKey, endpoints[^1].TrustedHostKey);
        Assert.Equal(OtherKey, vm.Accept()!.HostKeySha256);
    }

    [Fact]
    public void TrustedHostKey_StaysWithItsHostAndPort()
    {
        var existing = new HostedConnection { Protocol = HostedProtocol.Sftp, Host = Host, Port = 2022, User = "gp_user", HostKeySha256 = HostKey };
        var vm = Model(existing: existing);
        Assert.Equal(HostKey, vm.Accept()!.HostKeySha256);

        vm.PortText = "22"; // на другом порту — другой sshd
        Assert.Null(vm.Accept()!.HostKeySha256);
        vm.PortText = "2022";
        Assert.Equal(HostKey, vm.Accept()!.HostKeySha256);

        vm.Address = "sftp://203.0.113.99";
        vm.PortText = "2022";
        Assert.Null(vm.Accept()!.HostKeySha256);

        vm.Address = Host; // FTP: ключ SSH к нему не относится
        Assert.Null(vm.Accept()!.HostKeySha256);
    }

    [Fact]
    public async Task FtpCertificatePrompt_StillSaysCertificate()
    {
        var vm = Model((_, _, _) => Task.FromException<HostedCheckResult>(
            new RemoteFilesException(RemoteFailure.CertificateUntrusted, "untrusted") { CertificateSha256 = new string('A', 64) }));
        vm.Address = Host;
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("hosted.trust"), vm.TrustText);
        await vm.TrustCertificateCommand.ExecuteAsync(null);
        Assert.Equal(new string('A', 64), vm.Accept()!.CertificateSha256);
        Assert.Null(vm.Accept()!.HostKeySha256);
    }

    // ===== правка готового подключения =====

    private HostedConnection ExistingKey() => new()
    {
        Protocol = HostedProtocol.Sftp, Host = Host, Port = 2022, User = "gp_user", KeyFile = KeyFile(),
        KeyPassphrase = HostedSecret.Protect("saved-passphrase"), HostKeySha256 = HostKey, Root = "/", ConfigPath = "serverconfig.json",
        ModDirs = ["Mods"], InstallDir = "Mods",
    };

    [Fact]
    public void Edit_KeyConnection_LoadsAsIs_KeepingItsPort()
    {
        var vm = Model(existing: ExistingKey());

        Assert.Equal("sftp://" + Host, vm.Address);
        Assert.True(vm.IsSftp);
        Assert.Equal("2022", vm.PortText); // у sftp:// без порта 22 — но сохранённый порт не затирается
        Assert.True(vm.IsKeyLogin);
        Assert.Equal(Path.Combine(_dir, "id_ed25519"), vm.KeyFile);
        Assert.Equal("", vm.KeyPassphrase); // пароль ключа в окно не возвращается
        Assert.True(vm.HasSavedKeyPassphrase);
        Assert.Equal(Loc.T("hosted.keyPassphraseKeep"), vm.KeyPassphraseNote);
        Assert.False(vm.ShowPasswordKeep);
        Assert.False(vm.IsAdvancedOpen);
        Assert.True(vm.CanAccept);
    }

    [Fact]
    public async Task Edit_EmptyPassphrase_KeepsTheSavedOne_AndTheCheckUsesIt()
    {
        var existing = ExistingKey();
        HostedEndpoint? endpoint = null;
        var vm = Model((e, _, _) => { endpoint = e; return Task.FromResult(Found()); }, existing);

        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal("saved-passphrase", endpoint!.KeyPassphrase);
        Assert.Equal(HostKey, endpoint.TrustedHostKey);

        var result = vm.Accept()!;
        Assert.Equal(existing.KeyPassphrase, result.KeyPassphrase);
        Assert.Equal(HostKey, result.HostKeySha256);
        Assert.Equal("serverconfig.json", result.ConfigPath);
    }

    [Fact]
    public async Task Edit_UnreadableSavedPassphrase_CheckSaysSo()
    {
        var existing = ExistingKey();
        existing.KeyPassphrase = "not-a-protected-secret";
        var calls = 0;
        var vm = Model((_, _, _) => { calls++; return Task.FromResult(Found()); }, existing);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(0, calls);
        Assert.Equal(Loc.T("hosted.keyPassphraseUnreadable"), vm.CheckText); // не прочитать именно пароль ключа
        vm.KeyPassphrase = "typed-again";
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Edit_SwitchingToPassword_DropsTheKeyAndItsPassphrase()
    {
        var vm = Model(existing: ExistingKey());
        vm.AuthIndex = 0;
        vm.Password = "made-up-password";

        var result = vm.Accept()!;

        Assert.Null(result.KeyFile);
        Assert.Null(result.KeyPassphrase);
        Assert.Equal("made-up-password", HostedSecret.Unprotect(result.Password));
    }

    [Fact]
    public void Edit_FtpToSftp_DropsThePinnedPaths()
    {
        var existing = new HostedConnection
        {
            Host = Host, Port = 58021, User = "gp_user", Password = HostedSecret.Protect("saved-password"), Tls = FtpTls.Explicit,
            CertificateSha256 = new string('C', 64), ConfigPath = "serverconfig.json", ModDirs = ["Mods"], InstallDir = "Mods",
        };
        var vm = Model(existing: existing);
        vm.Address = "sftp://" + Host;

        var result = vm.Accept()!;

        Assert.Equal(HostedProtocol.Sftp, result.Protocol);
        Assert.Equal(22, result.Port);
        Assert.Null(result.ConfigPath); // у SFTP пути другие — раскладка найдётся заново
        Assert.Null(result.ModDirs);
        Assert.Null(result.HostKeySha256);
        Assert.Equal(existing.Password, result.Password); // пароль тот же — по паролю и войдём
    }
}

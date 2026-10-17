using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Versioning;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Окно «Сервер у хостера»: разбор адреса, подсказки по порту, правила кнопок, проверка подключения (подменена
/// делегатом, сети тесты не трогают), отмена устаревшей проверки, доверие к сертификату, правка готового подключения.
/// Адреса — из TEST-NET-3 (203.0.113.x), пароли выдуманные.
/// </summary>
public sealed class HostedConnectViewModelTests
{
    private const string Host = "203.0.113.40";

    private delegate Task<HostedCheckResult> Check(HostedEndpoint endpoint, HostedConnection settings, CancellationToken ct);

    private static HostedConnectViewModel Model(Check? check = null, HostedConnection? existing = null) =>
        new(existing, "Подключить", check is null
            ? (_, _, _) => throw new Xunit.Sdk.XunitException("проверка подключения в этом тесте не ожидалась")
            : new Func<HostedEndpoint, HostedConnection, CancellationToken, Task<HostedCheckResult>>(check));

    private static HostedCheckResult Found(bool encrypted = false, string version = "1.22.7", int mods = 12) =>
        new(new HostedLayout("/", "/serverconfig.json", "/", "/", ["/Mods"], "/Mods", []), ModVersion.ParseOrNull(version),
            GameVersionSource.VersionFile, mods, new RemoteFeatures(true, true, encrypted, null));

    private static HostedConnectViewModel Filled(Check? check = null, HostedConnection? existing = null)
    {
        var vm = Model(check, existing);
        vm.Address = Host;
        vm.PortText = "58021";
        vm.User = "gp_user";
        vm.Password = "made-up-password";
        return vm;
    }

    // ===== разбор адреса =====

    [Theory]
    [InlineData("203.0.113.40", "ftp", "203.0.113.40", null, null, null)]
    [InlineData("  my_host.example.com  ", "ftp", "my_host.example.com", null, null, null)]
    [InlineData("203.0.113.40:58021", "ftp", "203.0.113.40", 58021, null, null)]
    [InlineData("ftp://gp_user@203.0.113.40:58021/g123456", "ftp", "203.0.113.40", 58021, "gp_user", "/g123456")]
    [InlineData("FTP://Host.Example.COM", "ftp", "host.example.com", null, null, null)]
    [InlineData("ftps://203.0.113.40:990", "ftps", "203.0.113.40", 990, null, null)]
    [InlineData("sftp://user@203.0.113.40:2022", "sftp", "203.0.113.40", 2022, "user", null)]
    [InlineData("[2001:db8::1]:2121", "ftp", "2001:db8::1", 2121, null, null)]
    public void ParseAddress_SplitsHostPortUserAndPath(string text, string scheme, string host, int? port, string? user, string? path)
    {
        var parts = HostedConnectViewModel.ParseAddress(text);
        Assert.NotNull(parts);
        Assert.Equal(scheme, parts.Scheme);
        Assert.Equal(host, parts.Host);
        Assert.Equal(port, parts.Port);
        Assert.Equal(user, parts.User);
        Assert.Equal(path, parts.Path);
    }

    [Fact]
    public void ParseAddress_UrlDecodesLoginAndPassword()
    {
        var parts = HostedConnectViewModel.ParseAddress("ftp://gp%40user:p%3Ass%2Fw@203.0.113.40");
        Assert.NotNull(parts);
        Assert.Equal("gp@user", parts.User);
        Assert.Equal("p:ss/w", parts.Password);
        Assert.Equal("203.0.113.40", parts.Host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two words")]
    [InlineData("http://203.0.113.40")]
    [InlineData("203.0.113.40:0")]
    [InlineData("203.0.113.40:99999")]
    [InlineData("203.0.113.40:ftp")]
    [InlineData("ftp://")]
    [InlineData("-bad.example.com")]
    [InlineData("[2001:db8::1")]
    public void ParseAddress_RejectsNonAddresses(string text) => Assert.Null(HostedConnectViewModel.ParseAddress(text));

    [Fact]
    public void TypingAUrl_FillsTheFields_AndLeavingTheFieldKeepsOnlyTheHost()
    {
        var vm = Model();
        vm.Address = "ftp://gp_user:made-up-password@203.0.113.40:58021/g123456";

        Assert.Equal(Host, vm.Host);
        Assert.Equal("58021", vm.PortText);
        Assert.Equal("gp_user", vm.User);
        Assert.Equal("made-up-password", vm.Password);
        Assert.Equal("/g123456", vm.Root);
        Assert.True(vm.IsAdvancedOpen); // папка не по умолчанию — «Дополнительно» не прячет её
        Assert.Contains("made-up-password", vm.Address); // пока набираем, адрес не переписывается

        vm.CommitAddress();
        Assert.Equal(Host, vm.Address); // пароля и логина в видимом поле не остаётся
        Assert.Equal("gp_user", vm.User);
        Assert.Equal("58021", vm.PortText);
    }

    [Fact]
    public void FtpsUrl_MeansImplicitTls_AndPort990()
    {
        var vm = Model();
        vm.Address = "ftps://" + Host;
        Assert.Equal(FtpTls.Implicit, vm.Tls);
        Assert.Equal("990", vm.PortText);
        Assert.Equal(Loc.T("hosted.portImplicit"), vm.PortHint);

        vm.Address = "ftps://" + Host + ":21990";
        Assert.Equal("21990", vm.PortText);
    }

    [Fact]
    public void SftpUrl_ExplainsAndBlocksCheckAndOk()
    {
        var vm = Model();
        vm.Address = "sftp://" + Host + ":2022";

        Assert.True(vm.IsSftp);
        Assert.Equal(Loc.T("hosted.sftpLater"), vm.AddressHint);
        Assert.Equal(RowTone.Warn, vm.AddressTone);
        Assert.False(vm.CanAccept);
        Assert.False(vm.CheckCommand.CanExecute(null));
        Assert.Null(vm.Accept());

        vm.CommitAddress(); // sftp:// остаётся в поле: иначе пропадёт объяснение
        Assert.StartsWith("sftp://", vm.Address);

        vm.Address = Host; // исправили на обычный адрес
        Assert.False(vm.IsSftp);
        Assert.True(vm.CanAccept);
    }

    [Fact]
    public void BadAddress_ShowsTheFormatInRed_AndBlocksOk()
    {
        var vm = Model();
        Assert.Equal(RowTone.Muted, vm.AddressTone); // пусто — формат серым
        vm.Address = "http://x";
        Assert.Equal(Loc.T("hosted.hostHint"), vm.AddressHint);
        Assert.Equal(RowTone.Danger, vm.AddressTone);
        Assert.False(vm.CanAccept);
    }

    // ===== порт и правила кнопок =====

    [Theory]
    [InlineData("21", "hosted.portHintFtp")]
    [InlineData("8821", "hosted.portHintTcadmin")]
    [InlineData("990", "hosted.portImplicit")]
    [InlineData("58021", null)]
    public void PortHints_NameTheUsualPorts(string port, string? key)
    {
        var vm = Model();
        vm.PortText = port;
        Assert.Equal(key is null ? "" : Loc.T(key), vm.PortHint);
    }

    [Theory]
    [InlineData("22")]
    [InlineData("2022")]
    [InlineData("8822")]
    public void PortHints_SftpPorts_AreWarnings(string port)
    {
        var vm = Model();
        vm.PortText = port;
        Assert.Equal(Loc.T("hosted.portSftp", int.Parse(port)), vm.PortHint);
        Assert.Equal(RowTone.Warn, vm.PortTone);
    }

    [Fact]
    public void OkRules_NeedHost_PortAndFolder()
    {
        var vm = Model();
        Assert.False(vm.CanAccept);
        Assert.False(vm.CheckCommand.CanExecute(null));
        Assert.Null(vm.Accept());

        vm.Address = Host;
        Assert.True(vm.CanAccept); // порт по умолчанию — 21
        Assert.True(vm.CheckCommand.CanExecute(null));

        vm.PortText = "0";
        Assert.False(vm.CanAccept);
        vm.PortText = "70000";
        Assert.False(vm.CanAccept);
        vm.PortText = "abc";
        Assert.False(vm.CanAccept);
        vm.PortText = "21";
        Assert.True(vm.CanAccept);

        vm.Root = "/a/../b";
        Assert.False(vm.CanAccept);
        Assert.Equal(RowTone.Danger, vm.RootTone);
        vm.Root = "  ";
        Assert.True(vm.CanAccept); // пусто — корень
        Assert.Equal("/", vm.NormalizedRoot);

        vm.Address = "";
        Assert.False(vm.CanAccept);
    }

    [Fact]
    public void Accept_BuildsTheConnection_WithEncryptedPassword()
    {
        var vm = Filled();
        vm.TlsIndex = 2; // без шифрования
        vm.ActiveMode = true;
        vm.Root = "g123456/";

        var result = vm.Accept();
        Assert.NotNull(result);
        Assert.Same(result, vm.Result);
        Assert.Equal(HostedProtocol.Ftp, result.Protocol);
        Assert.Equal(Host, result.Host);
        Assert.Equal(58021, result.Port);
        Assert.Equal(FtpTls.None, result.Tls);
        Assert.Equal("gp_user", result.User);
        Assert.Equal("/g123456", result.Root);
        Assert.True(result.ActiveMode);
        Assert.NotNull(result.Password);
        Assert.NotEqual("made-up-password", result.Password); // в настройки уходит зашифрованным
        Assert.Equal("made-up-password", HostedSecret.Unprotect(result.Password));
        Assert.DoesNotContain("made-up-password", result.ToString());
        Assert.Null(result.ConfigPath); // проверки не было — путей закреплять нечего
    }

    [Fact]
    public void Accept_WithoutPassword_StoresNone()
    {
        var vm = Model();
        vm.Address = Host;
        Assert.Null(vm.Accept()!.Password);
    }

    // ===== проверка подключения =====

    [Fact]
    public async Task Check_Success_ShowsOneLine_AndPinsThePathsOnOk()
    {
        HostedEndpoint? endpoint = null;
        HostedConnection? settings = null;
        var vm = Filled((e, s, _) => { endpoint = e; settings = s; return Task.FromResult(Found()); });

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(Host, endpoint!.Host);
        Assert.Equal(58021, endpoint.Port);
        Assert.Equal("gp_user", endpoint.User);
        Assert.Equal("made-up-password", endpoint.Password);
        Assert.Equal("/", settings!.Root);
        Assert.Equal(RowTone.Good, vm.CheckTone);
        Assert.False(vm.IsChecking);
        Assert.Equal(Loc.T("hosted.checkOk", Loc.T("hosted.noEncryption"), "/serverconfig.json", "/Mods (12)",
            "1.22.7 (" + Loc.T("hosted.versionSourceFile") + ")"), vm.CheckText);
        Assert.DoesNotContain('\n', vm.CheckText);
        Assert.DoesNotContain("made-up-password", vm.CheckText);

        var result = vm.Accept()!;
        Assert.Equal("serverconfig.json", result.ConfigPath);
        Assert.Equal(["Mods"], result.ModDirs);
        Assert.Equal("Mods", result.InstallDir);
        Assert.Equal("1.22.7", result.FoundGameVersion); // её видно сразу, ещё до чтения модов
    }

    [Fact]
    public async Task Check_PinnedPaths_AreDroppedWhenTheFieldsChangeAfterwards()
    {
        var vm = Filled((_, _, _) => Task.FromResult(Found()));
        await vm.CheckCommand.ExecuteAsync(null);
        vm.User = "someone_else";

        var result = vm.Accept()!;
        Assert.Null(result.ConfigPath); // раскладка найдена для других полей
        Assert.Null(result.FoundGameVersion); // и версия игры — тоже
        Assert.Equal("", vm.CheckText);
    }

    [Fact]
    public async Task Check_AutoWithTls_BecomesExplicit_SoItNeverSilentlyDowngrades()
    {
        var vm = Filled((_, _, _) => Task.FromResult(Found(encrypted: true)));
        Assert.Equal(FtpTls.Auto, vm.Tls);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(FtpTls.Explicit, vm.Tls);
        Assert.Equal(RowTone.Good, vm.CheckTone); // смена пункта списка не стирает итог проверки
        Assert.Contains(Loc.T("hosted.encrypted"), vm.CheckText);
        Assert.Equal(FtpTls.Explicit, vm.Accept()!.Tls);
    }

    [Fact]
    public async Task Check_AutoWithoutTls_StaysAuto()
    {
        var vm = Filled((_, _, _) => Task.FromResult(Found(encrypted: false)));
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(FtpTls.Auto, vm.Accept()!.Tls);
    }

    [Fact]
    public async Task Check_ExplicitMode_IsNotChanged()
    {
        var vm = Filled((_, _, _) => Task.FromResult(Found(encrypted: true)));
        vm.TlsIndex = 3; // неявный TLS
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(FtpTls.Implicit, vm.Accept()!.Tls);
    }

    [Fact]
    public async Task Check_Failure_IsSaidInWords_NotThrown()
    {
        var vm = Filled((_, _, _) => Task.FromException<HostedCheckResult>(new RemoteFilesException(RemoteFailure.Auth, "530 Login incorrect")));

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(Loc.T("hosted.errAuth"), vm.CheckText);
        Assert.Equal(RowTone.Danger, vm.CheckTone);
        Assert.False(vm.IsChecking);
        Assert.False(vm.HasPendingCertificate);
        Assert.NotNull(vm.Accept()); // «ОК» от проверки не зависит: подключить можно и непроверенным
    }

    [Fact]
    public async Task Check_WhileRunning_ShowsChecking_AndBlocksASecondRun()
    {
        var gate = new TaskCompletionSource<HostedCheckResult>();
        var vm = Filled((_, _, _) => gate.Task);

        var running = vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.IsChecking);
        Assert.Equal(Loc.T("hosted.checking"), vm.CheckText);
        Assert.False(vm.CheckCommand.CanExecute(null));

        gate.SetResult(Found());
        await running;
        Assert.False(vm.IsChecking);
        Assert.Equal(RowTone.Good, vm.CheckTone);
    }

    [Fact]
    public async Task Check_StaleAnswer_IsDropped_WhenTheFieldsChange()
    {
        var gate = new TaskCompletionSource<HostedCheckResult>();
        CancellationToken seen = default;
        var vm = Filled((_, _, ct) => { seen = ct; return gate.Task; });

        var running = vm.CheckCommand.ExecuteAsync(null);
        vm.Address = "203.0.113.41"; // другой сервер: ответ первого уже ни о чём

        Assert.True(seen.IsCancellationRequested);
        Assert.False(vm.IsChecking);
        Assert.Equal("", vm.CheckText);
        gate.SetResult(Found());
        await running;

        Assert.Equal("", vm.CheckText); // поздний ответ ничего не показал
        Assert.False(vm.IsChecking);
        Assert.Null(vm.Accept()!.ConfigPath); // и пути из него не закрепились
        Assert.True(vm.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task Check_Cancelled_ByCancelCheck_LeavesNoTrace()
    {
        var gate = new TaskCompletionSource<HostedCheckResult>();
        var vm = Filled((_, _, ct) => gate.Task.WaitAsync(ct));
        var running = vm.CheckCommand.ExecuteAsync(null);

        vm.CancelCheck(); // окно закрыли
        await running;

        Assert.False(vm.IsChecking);
        Assert.Equal("", vm.CheckText);
    }

    // ===== сертификат =====

    [Fact]
    public async Task UntrustedCertificate_ShowsFingerprint_AndTrustRechecksWithIt()
    {
        var sha = new string('A', 64);
        var endpoints = new List<HostedEndpoint>();
        var vm = Filled((e, _, _) =>
        {
            endpoints.Add(e);
            return e.TrustedCertificateSha256 == sha
                ? Task.FromResult(Found(encrypted: true))
                : Task.FromException<HostedCheckResult>(new RemoteFilesException(RemoteFailure.CertificateUntrusted, "untrusted") { CertificateSha256 = sha });
        });
        vm.TlsIndex = 1; // только с шифрованием

        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingCertificate);
        Assert.Contains(sha, vm.CertificateText);
        Assert.Equal("", vm.CheckText);
        Assert.Null(endpoints[0].TrustedCertificateSha256);

        await vm.TrustCertificateCommand.ExecuteAsync(null);

        Assert.Equal(2, endpoints.Count);
        Assert.Equal(sha, endpoints[1].TrustedCertificateSha256);
        Assert.False(vm.HasPendingCertificate);
        Assert.Equal(RowTone.Good, vm.CheckTone);
        Assert.Equal(sha, vm.Accept()!.CertificateSha256);
    }

    [Fact]
    public async Task UntrustedCertificate_IsForgotten_WhenTheFieldsChange()
    {
        var vm = Filled((_, _, _) => Task.FromException<HostedCheckResult>(
            new RemoteFilesException(RemoteFailure.CertificateUntrusted, "untrusted") { CertificateSha256 = new string('B', 64) }));
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingCertificate);

        vm.User = "other";
        Assert.False(vm.HasPendingCertificate);
        Assert.Equal("", vm.CertificateText);
    }

    [Fact]
    public async Task TrustedCertificate_DoesNotCarryOverToAnotherHost()
    {
        var sha = new string('C', 64);
        var existing = new HostedConnection { Host = Host, Port = 21, User = "gp_user", Tls = FtpTls.Explicit, CertificateSha256 = sha };
        var vm = Model(existing: existing);
        Assert.Equal(sha, vm.Accept()!.CertificateSha256); // тот же сервер — доверие осталось

        vm.Address = "203.0.113.99";
        Assert.Null(vm.Accept()!.CertificateSha256); // другой сервер — другой сертификат
        await Task.CompletedTask;
    }

    // ===== правка готового подключения =====

    private static HostedConnection Existing() => new()
    {
        Host = Host, Port = 58021, Tls = FtpTls.None, User = "gp_user", Password = HostedSecret.Protect("saved-password"),
        Root = "/g123456", ActiveMode = true, ConfigPath = "serverconfig.json", ModDirs = ["Mods"], InstallDir = "Mods",
        GameVersion = "1.21.5", SkipStopReminder = true,
    };

    [Fact]
    public void Edit_LoadsTheFields_AndHintsThatTheEmptyPasswordKeepsTheOld()
    {
        var vm = Model(existing: Existing());

        Assert.Equal(Host, vm.Address);
        Assert.Equal("58021", vm.PortText);
        Assert.Equal(FtpTls.None, vm.Tls);
        Assert.Equal("gp_user", vm.User);
        Assert.Equal("", vm.Password); // пароль в окно не возвращается
        Assert.Equal("/g123456", vm.Root);
        Assert.True(vm.ActiveMode);
        Assert.True(vm.IsAdvancedOpen);
        Assert.True(vm.HasSavedPassword);
        Assert.True(vm.CanAccept);
        Assert.Equal("Подключить", vm.OkText);

        Assert.False(Model().HasSavedPassword);
    }

    [Fact]
    public void Edit_EmptyPassword_KeepsTheOldOne_AndTheRestOfTheProfile()
    {
        var existing = Existing();
        var oldPassword = existing.Password;
        var vm = Model(existing: existing);
        vm.User = "new_login";

        var result = vm.Accept()!;

        Assert.Equal(oldPassword, result.Password);
        Assert.Equal("new_login", result.User);
        Assert.Equal("1.21.5", result.GameVersion); // версия, указанная вручную, и «не напоминать» — не окно подключения
        Assert.True(result.SkipStopReminder);
        Assert.Equal("serverconfig.json", result.ConfigPath); // сервер тот же — закреплённые пути живы
        Assert.Equal(["Mods"], result.ModDirs);
        Assert.NotSame(existing, result);
        Assert.Equal("gp_user", existing.User); // исходное подключение не тронуто
    }

    [Fact]
    public void Edit_NewPassword_ReplacesTheOld()
    {
        var existing = Existing();
        var vm = Model(existing: existing);
        vm.Password = "another-password";

        var result = vm.Accept()!;

        Assert.NotEqual(existing.Password, result.Password);
        Assert.Equal("another-password", HostedSecret.Unprotect(result.Password));
    }

    [Fact]
    public void Edit_AnotherHostOrFolder_DropsThePinnedPaths()
    {
        var vm = Model(existing: Existing());
        vm.Address = "203.0.113.77";
        var result = vm.Accept()!;
        Assert.Null(result.ConfigPath);
        Assert.Null(result.ModDirs);
        Assert.Null(result.InstallDir);

        var vm2 = Model(existing: Existing());
        vm2.Root = "/g999";
        Assert.Null(vm2.Accept()!.ConfigPath);
    }

    [Fact]
    public async Task Edit_Check_UsesTheSavedPassword_WhenTheFieldIsEmpty()
    {
        HostedEndpoint? endpoint = null;
        var vm = Model((e, _, _) => { endpoint = e; return Task.FromResult(Found()); }, Existing());

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal("saved-password", endpoint!.Password);
        Assert.DoesNotContain("saved-password", endpoint.ToString());
        Assert.DoesNotContain("saved-password", vm.CheckText);
    }

    [Fact]
    public void Edit_UnreadableSavedPassword_IsKeptUntilANewOneIsTyped()
    {
        var existing = Existing();
        existing.Password = "not-a-protected-secret"; // как после переноса папки данных на другой компьютер
        var vm = Model(existing: existing);
        Assert.True(vm.CanAccept);
        Assert.Equal("not-a-protected-secret", vm.Accept()!.Password); // сохранённое не затираем, пока человек не ввёл новый
    }

    [Fact]
    public void Edit_DoesNotChangeTheConnectionItWasGiven()
    {
        var existing = Existing();
        var vm = Model(existing: existing);
        vm.Address = "203.0.113.5";
        vm.PortText = "21";
        vm.Root = "/";
        vm.ActiveMode = false;
        vm.Accept();

        Assert.Equal(Host, existing.Host);
        Assert.Equal(58021, existing.Port);
        Assert.Equal("/g123456", existing.Root);
        Assert.True(existing.ActiveMode);
    }

    // ===== разбор адреса: опечатки и секреты =====

    [Theory]
    [InlineData("203.0.113.40:21:5")]
    [InlineData("host.example.com:21:22")]
    [InlineData("203.0.113.40:21:")]
    [InlineData("[host.example.com]:21")]
    public void ParseAddress_RejectsExtraColons_UnlessItIsIPv6(string text) => Assert.Null(HostedConnectViewModel.ParseAddress(text));

    [Fact]
    public void ParseAddress_TakesABareIPv6_AsTheHost()
    {
        var parts = HostedConnectViewModel.ParseAddress("2001:db8::1");
        Assert.NotNull(parts);
        Assert.Equal("2001:db8::1", parts.Host);
        Assert.Null(parts.Port);
    }

    [Fact]
    public void AddressParts_ToString_DoesNotPrintThePassword()
    {
        var parts = HostedConnectViewModel.ParseAddress("ftp://gp_user:made-up-password@203.0.113.40:58021");
        Assert.NotNull(parts);
        Assert.Equal("made-up-password", parts.Password);
        Assert.DoesNotContain("made-up-password", parts.ToString());
        Assert.DoesNotContain("made-up-password", $"{parts}");
        Assert.Contains("gp_user", parts.ToString());
    }

    // ===== правка ссылки после вставки =====

    [Fact]
    public void EditingTheLink_PutsBackTheFieldsItNoLongerHas()
    {
        var vm = Model();
        vm.Address = "ftps://gp_user:made-up-password@203.0.113.40/g123456";
        Assert.Equal("990", vm.PortText);
        Assert.Equal(FtpTls.Implicit, vm.Tls);
        Assert.Equal("made-up-password", vm.Password);
        Assert.Equal("/g123456", vm.Root);

        vm.Address = "ftp://gp_user@203.0.113.40"; // убрали пароль и папку, ftps сменили на ftp

        Assert.Equal("21", vm.PortText);
        Assert.Equal(FtpTls.Auto, vm.Tls);
        Assert.Equal("", vm.Password);
        Assert.Equal("/", vm.Root);
        Assert.Equal("gp_user", vm.User); // логин в ссылке остался
    }

    [Fact]
    public void EditingTheLink_KeepsWhatThePersonTypedIntoTheFieldsThemselves()
    {
        var vm = Model();
        vm.Address = "ftp://gp_user:made-up-password@203.0.113.40:58021";
        vm.Password = "typed-by-hand";
        vm.PortText = "8821";

        vm.Address = "ftp://gp_user@203.0.113.40";

        Assert.Equal("typed-by-hand", vm.Password);
        Assert.Equal("8821", vm.PortText);
    }

    [Fact]
    public void LeavingTheField_MakesTheLinkFieldsTheirOwn()
    {
        var vm = Model();
        vm.Address = "ftp://gp_user:made-up-password@203.0.113.40:58021";
        vm.CommitAddress();

        Assert.Equal(Host, vm.Address);
        Assert.Equal("made-up-password", vm.Password); // адрес стал голым хостом — поля от этого не откатываются
        Assert.Equal("58021", vm.PortText);
    }

    [Fact]
    public void SftpLink_LeavingTheField_DropsLoginAndPasswordFromTheVisibleAddress()
    {
        var vm = Model();
        vm.Address = "sftp://user:made-up-password@203.0.113.40:2022";

        vm.CommitAddress();

        Assert.Equal("sftp://" + Host, vm.Address);
        Assert.DoesNotContain("made-up-password", vm.Address);
        Assert.True(vm.IsSftp);
        Assert.Equal(Loc.T("hosted.sftpLater"), vm.AddressHint);
    }

    // ===== проверка: причины словами =====

    [Fact]
    public async Task Edit_UnreadableSavedPassword_CheckSaysSo_InsteadOfConnectingWithAnEmptyOne()
    {
        var existing = Existing();
        existing.Password = "not-a-protected-secret";
        var calls = 0;
        var vm = Model((_, _, _) => { calls++; return Task.FromResult(Found()); }, existing);

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(0, calls);
        Assert.Equal(Loc.T("hosted.passwordUnreadable"), vm.CheckText);
        Assert.Equal(RowTone.Danger, vm.CheckTone);
        Assert.False(vm.IsChecking);

        vm.Password = "new-password"; // ввели заново — проверка идёт
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Check_TransportTimeout_IsSaidInTheWindowLanguage()
    {
        var vm = Filled((_, _, _) => Task.FromException<HostedCheckResult>(new TaskCanceledException("A task was canceled.")));

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(Loc.T("hosted.errTimeout"), vm.CheckText);
        Assert.Equal(RowTone.Danger, vm.CheckTone);
    }

    [Fact]
    public async Task Check_TimeoutException_IsSaidInWords()
    {
        var vm = Filled((_, _, _) => Task.FromException<HostedCheckResult>(new TimeoutException("The operation has timed out.")));

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(Loc.T("hosted.errTimeout"), vm.CheckText);
    }
}

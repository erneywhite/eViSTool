using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using Newtonsoft.Json;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// Подключение SFTP в модели: файл ключа и пароль ключа (зашифрован, как пароль), отпечаток ключа сервера отдельно от
/// сертификата FTPS, тексты отказов словами и ни одного секрета в строках.
/// </summary>
public sealed class HostedSftpModelTests
{
    private static readonly string HostKey = "SHA256:" + new string('K', 43);

    private static HostedConnection Sftp() => new()
    {
        Protocol = HostedProtocol.Sftp, Host = " 203.0.113.40 ", Port = 2022, User = "gp_user",
        KeyFile = "/home/me/.ssh/id_ed25519", KeyPassphrase = HostedSecret.Protect("made-up-passphrase"), HostKeySha256 = HostKey,
    };

    [Fact]
    public void Endpoint_DecryptsThePassphrase_AndCarriesTheKeyAndHostKey()
    {
        var endpoint = HostedEndpoint.From(Sftp())!;

        Assert.Equal(HostedProtocol.Sftp, endpoint.Protocol);
        Assert.Equal("203.0.113.40", endpoint.Host);
        Assert.Equal("/home/me/.ssh/id_ed25519", endpoint.KeyFile);
        Assert.Equal("made-up-passphrase", endpoint.KeyPassphrase);
        Assert.Equal(HostKey, endpoint.TrustedHostKey);
        Assert.Equal("", endpoint.Password);
        Assert.Null(endpoint.TrustedCertificateSha256);
    }

    [Fact]
    public void Endpoint_UnreadablePassphrase_IsNull_LikeAnUnreadablePassword()
    {
        var connection = Sftp();
        connection.KeyPassphrase = "not-a-protected-secret";
        Assert.Null(HostedEndpoint.From(connection));
    }

    [Fact]
    public void Endpoint_WithoutKey_IsPasswordLogin_AndTildeIsTheUserFolder()
    {
        var connection = Sftp();
        (connection.KeyFile, connection.KeyPassphrase) = ("   ", null);
        var endpoint = HostedEndpoint.From(connection)!;
        Assert.Null(endpoint.KeyFile);
        Assert.Null(endpoint.KeyPassphrase);

        connection.KeyFile = "~/.ssh/id_ed25519";
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh/id_ed25519"),
            HostedEndpoint.From(connection)!.KeyFile);
    }

    [Fact]
    public void Endpoint_OtherPassphraseOrKey_IsAnotherConnection()
    {
        // пул соединений сверяет описания целиком: сменили ключ — старое соединение не годится
        var a = HostedEndpoint.From(Sftp())!;
        Assert.Equal(a, HostedEndpoint.From(Sftp()));
        Assert.NotEqual(a, a with { KeyPassphrase = "other" });
        Assert.NotEqual(a, a with { KeyFile = "/other" });
        Assert.NotEqual(a, a with { TrustedHostKey = null });
    }

    [Fact]
    public void ToString_NeverShowsTheSecrets()
    {
        var connection = Sftp();
        connection.Password = HostedSecret.Protect("made-up-password");
        var endpoint = HostedEndpoint.From(connection)!;
        var profile = new GameProfile { Name = "SFTP", Kind = ProfileKind.Server, RemoteCode = GameProfile.HostedMarker, Hosted = connection };
        var target = ModTarget.For(profile)!;

        foreach (var text in new[] { endpoint.ToString(), $"{endpoint}", connection.ToString(), target.ToString(), target.Hosted!.ToString() })
        {
            Assert.DoesNotContain("made-up-passphrase", text);
            Assert.DoesNotContain("made-up-password", text);
            Assert.DoesNotContain(connection.KeyPassphrase!, text);
        }
        Assert.Equal("sftp://gp_user@203.0.113.40:2022 (key)", endpoint.ToString());
        Assert.Equal("sftp://gp_user@203.0.113.40:2022 (password)", (endpoint with { KeyFile = null }).ToString());
    }

    [Fact]
    public void Settings_RoundTrip_KeepTheSftpFields_AndThePassphraseEncrypted()
    {
        var connection = Sftp();
        var json = JsonConvert.SerializeObject(connection);
        Assert.DoesNotContain("made-up-passphrase", json);
        Assert.Contains("\"Protocol\":\"Sftp\"", json);

        var back = JsonConvert.DeserializeObject<HostedConnection>(json)!;
        Assert.Equal(connection.KeyFile, back.KeyFile);
        Assert.Equal(HostKey, back.HostKeySha256);
        Assert.Equal("made-up-passphrase", HostedSecret.Unprotect(back.KeyPassphrase));

        // подключение FTP прошлых версий: новых полей в файле нет — и они не появляются
        var ftp = JsonConvert.SerializeObject(new HostedConnection { Host = "203.0.113.5" });
        Assert.DoesNotContain("KeyFile", ftp);
        Assert.DoesNotContain("KeyPassphrase", ftp);
        Assert.DoesNotContain("HostKeySha256", ftp);
        var old = JsonConvert.DeserializeObject<HostedConnection>("""{ "Protocol": "Ftp", "Host": "203.0.113.5", "Port": 21 }""")!;
        Assert.Null(old.KeyFile);
        Assert.Null(old.HostKeySha256);
    }

    [Fact]
    public void Clone_CopiesTheSftpFields()
    {
        var copy = Sftp().Clone();
        Assert.Equal("/home/me/.ssh/id_ed25519", copy.KeyFile);
        Assert.Equal(HostKey, copy.HostKeySha256);
        Assert.NotNull(copy.KeyPassphrase);
    }

    [Fact]
    public void Describe_HostKeyAndKeyFileFailures_InWords()
    {
        var unknown = HostedSecret.Describe(new RemoteFilesException(RemoteFailure.CertificateUntrusted, "x")
        {
            CertificateSha256 = HostKey, IsHostKey = true,
        });
        HostedTestText.AssertIs("hosted.trustHostKey", unknown.Replace(HostKey, "{0}"));

        var changed = HostedSecret.Describe(new RemoteFilesException(RemoteFailure.CertificateUntrusted, "x")
        {
            CertificateSha256 = HostKey, IsHostKey = true, HostKeyChanged = true,
        });
        HostedTestText.AssertIs("hosted.hostKeyChanged", changed.Replace(HostKey, "{0}"));

        // сертификат FTPS — по-прежнему своими словами
        var cert = HostedSecret.Describe(new RemoteFilesException(RemoteFailure.CertificateUntrusted, "x") { CertificateSha256 = "ab12" });
        HostedTestText.AssertIs("hosted.trustCert", cert.Replace("ab12", "{0}"));

        HostedTestText.AssertIs("hosted.errKeyPassphrase", HostedSecret.Describe(new RemoteFilesException(RemoteFailure.KeyPassphrase, "wrong")));
        var keyFile = HostedSecret.Describe(new RemoteFilesException(RemoteFailure.KeyFile, "/k: Invalid private key file."));
        HostedTestText.AssertIs("hosted.errKeyFile", keyFile.Replace("/k: Invalid private key file.", "{0}"));
    }

    [Fact]
    public void SameServer_IgnoresTheKeyAndHostKey()
    {
        // сменили ключ входа или доверились новому ключу сервера — кэш и журнал установки профиля остаются годными
        var a = Sftp();
        var b = Sftp();
        (b.KeyFile, b.KeyPassphrase, b.HostKeySha256) = ("/other", null, null);
        Assert.True(a.IsSameServer(b));
    }
}

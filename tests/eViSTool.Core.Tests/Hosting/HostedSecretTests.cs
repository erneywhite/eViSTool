using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;
using eViSTool.Core.Platform;
using eViSTool.Core.Server.Remote;

namespace eViSTool.Core.Tests.Hosting;

public sealed class HostedSecretTests
{
    [Fact]
    public void Password_IsStoredEncrypted_AndReadBack()
    {
        var stored = HostedSecret.Protect("Pa$$-слово:42");
        Assert.DoesNotContain("Pa$$", stored);
        Assert.Equal("Pa$$-слово:42", HostedSecret.Unprotect(stored));
        if (!OperatingSystem.IsWindows()) Assert.StartsWith(SecretKeyFile.Prefix, stored); // на Linux — AES-GCM, не DPAPI
    }

    [Fact]
    public void OtherPurpose_DoesNotDecrypt()
    {
        // пароль FTP не читается как код агента и наоборот: у них разные назначения
        var password = HostedSecret.Protect("evis1.not-a-code");
        Assert.Null(RemoteSecret.Unprotect(password));
        Assert.Null(SecretProtector.Unprotect(password, "eViSTool remote connection code"u8.ToArray()));

        var code = RemoteSecret.Protect(new ConnectionCode("192.0.2.1", 40000, RemoteAccess.NewKey(), new string('d', 64)));
        Assert.Null(HostedSecret.Unprotect(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hosted")]
    [InlineData("aes1:AAAA")]
    [InlineData("не base64")]
    public void Garbage_IsNull(string? stored) => Assert.Null(HostedSecret.Unprotect(stored));

    [Fact]
    public void Describe_MapsFailuresToWords()
    {
        HostedTestText.AssertIs("hosted.errAuth", HostedSecret.Describe(new RemoteFilesException(RemoteFailure.Auth, "530 Login incorrect")));
        HostedTestText.AssertIs("hosted.errTimeout", HostedSecret.Describe(new RemoteFilesException(RemoteFailure.Timeout, "t")));
        HostedTestText.AssertIs("hosted.errQuota", HostedSecret.Describe(new RemoteFilesException(RemoteFailure.Quota, "552")));
        HostedTestText.AssertIs("hosted.errNoTransport", HostedSecret.Describe(new RemoteFilesException(RemoteFailure.Unsupported, "x")));
        var connect = HostedSecret.Describe(new RemoteFilesException(RemoteFailure.Connect, "refused"));
        Assert.Contains("refused", connect);
        Assert.Contains(HostedTestText.Of("hosted.errConnect"), t => connect == t.Replace("{0}", "refused"));
        var cert = HostedSecret.Describe(new RemoteFilesException(RemoteFailure.CertificateUntrusted, "x") { CertificateSha256 = "ab12" });
        Assert.Contains("ab12", cert);
        Assert.Equal("plain", HostedSecret.Describe(new InvalidOperationException("plain")));
    }

    [Fact]
    public void RemoteFilesException_IsAnIOException()
    {
        // фильтры catch по всему окну ловят IOException: обрыв FTP не должен попасть в журнал вылетов
        IOException ex = new RemoteFilesException(RemoteFailure.Connect, "x");
        Assert.Equal(RemoteFailure.Connect, ((RemoteFilesException)ex).Kind);
    }

    [Fact]
    public void ToString_NeverShowsSecrets()
    {
        const string password = "Hunter2-Пароль";
        var profile = HostedSettingsCompatTests.HostedProfile(password);
        var target = ModTarget.For(profile)!;

        foreach (var text in new[]
                 {
                     target.ToString(), target.Hosted!.ToString(), target.Hosted.Endpoint.ToString(), profile.Hosted!.ToString(),
                     profile.Hosted.Describe(), $"{target}", $"{target.Hosted}", $"{target.Hosted.Endpoint}",
                 })
        {
            Assert.DoesNotContain(password, text);
            Assert.DoesNotContain(profile.Hosted.Password!, text); // и зашифрованного тоже
        }
        Assert.Contains("198.51.100.7:58021", target.Hosted.Endpoint.ToString());
        Assert.Equal("ftp://gp_user@198.51.100.7:58021", profile.Hosted.Describe());

        // и ключ агента: запись печатала код подключения целиком
        var code = new ConnectionCode("192.0.2.10", 42001, RemoteAccess.NewKey(), new string('e', 64));
        var agent = ModTarget.For(new Profiles.GameProfile { Name = "Дальний", Kind = Profiles.ProfileKind.Server, RemoteCode = RemoteSecret.Protect(code) })!;
        Assert.DoesNotContain(code.Key, agent.ToString());
        Assert.DoesNotContain(code.ToString(), agent.ToString());
        Assert.DoesNotContain(code.Fingerprint, agent.ToString());
        Assert.Contains("192.0.2.10:42001", agent.ToString());
    }

    [Fact]
    public void Describe_ShowsSchemeAndRoot()
    {
        Assert.Equal("ftps://198.51.100.1:990/g123456",
            new HostedConnection { Host = "198.51.100.1", Port = 990, Tls = FtpTls.Implicit, Root = "/g123456" }.Describe());
        Assert.Equal("sftp://u@198.51.100.1:22", new HostedConnection { Protocol = HostedProtocol.Sftp, Host = "198.51.100.1", Port = 22, User = "u" }.Describe());
    }
}

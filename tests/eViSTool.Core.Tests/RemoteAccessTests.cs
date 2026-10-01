using eViSTool.Core.Server.Remote;

namespace eViSTool.Core.Tests;

public sealed class RemoteAccessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evistool-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void ConnectionCode_RoundTrips_AndRejectsGarbage()
    {
        var code = new ConnectionCode("192.168.31.31", 48213, RemoteAccess.NewKey(), new string('a', 64));
        var text = code.ToString();

        Assert.StartsWith(ConnectionCode.Prefix, text);
        Assert.DoesNotContain(code.Key, text); // ключ не лежит в коде открытым текстом
        Assert.True(ConnectionCode.TryParse("  " + text + "\n", out var parsed));
        Assert.Equal(code, parsed);

        foreach (var bad in new[] { "", "evis1.", "evis1.!!!", "evis2." + text[6..], text[..^6], "http://192.168.31.31:48213" })
            Assert.False(ConnectionCode.TryParse(bad, out _), bad);
        // порт вне диапазона и короткий отпечаток — тоже нет
        Assert.False(ConnectionCode.TryParse((code with { Port = 0 }).ToString(), out _));
        Assert.False(ConnectionCode.TryParse((code with { Fingerprint = "abc" }).ToString(), out _));
    }

    [Fact]
    public void Enable_PicksPortKeyAndCertificateOnce()
    {
        var first = RemoteAccess.Enable("p", _dir);
        Assert.True(first.Enabled);
        Assert.InRange(first.Port, RemoteAccess.MinPort, RemoteAccess.MaxPort);
        Assert.True(first.Key.Length >= 32);
        using var cert = RemoteAccess.EnsureCertificate("p", _dir);
        var fingerprint = RemoteAccess.Fingerprint(cert);
        Assert.Equal(64, fingerprint.Length);
        Assert.True(cert.HasPrivateKey);

        // выключили и включили — порт, ключ и сертификат прежние: старый код подключения продолжает работать
        RemoteAccess.Disable("p", _dir);
        Assert.False(RemoteAccess.Load("p", _dir).Enabled);
        var again = RemoteAccess.Enable("p", _dir);
        Assert.Equal((first.Port, first.Key), (again.Port, again.Key));
        using (var same = RemoteAccess.EnsureCertificate("p", _dir)) Assert.Equal(fingerprint, RemoteAccess.Fingerprint(same));

        // новый ключ — прежний код больше не подходит, порт тот же
        var rotated = RemoteAccess.RegenerateKey("p", _dir);
        Assert.NotEqual(first.Key, rotated.Key);
        Assert.Equal(first.Port, rotated.Port);

        // у другого профиля — свои порт, ключ и сертификат
        var other = RemoteAccess.Enable("q", _dir);
        Assert.NotEqual(first.Key, other.Key);
        using var otherCert = RemoteAccess.EnsureCertificate("q", _dir);
        Assert.NotEqual(fingerprint, RemoteAccess.Fingerprint(otherCert));
    }

    [Fact]
    public void LocalAddresses_AreIPv4WithoutLoopback()
    {
        foreach (var a in RemoteAccess.LocalAddresses())
        {
            Assert.True(System.Net.IPAddress.TryParse(a, out var ip), a);
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, ip.AddressFamily);
            Assert.False(System.Net.IPAddress.IsLoopback(ip));
        }
    }

    [Fact]
    public void RemoteProfile_StoresCodeEncrypted()
    {
        var code = new ConnectionCode("192.168.31.31", 48213, RemoteAccess.NewKey(), new string('b', 64));
        var stored = RemoteSecret.Protect(code);

        Assert.DoesNotContain(code.Key, stored);
        Assert.DoesNotContain("192.168", stored);
        Assert.Equal(code, RemoteSecret.Unprotect(stored));
        Assert.Null(RemoteSecret.Unprotect("не base64"));
        Assert.Null(RemoteSecret.Unprotect(Convert.ToBase64String("чужое"u8.ToArray())));

        var profile = new eViSTool.Core.Profiles.GameProfile { Name = "VM", Kind = eViSTool.Core.Profiles.ProfileKind.Server, RemoteCode = stored };
        Assert.True(profile.IsRemote);
        var resolved = eViSTool.Core.Profiles.ProfileResolver.Resolve(profile);
        Assert.Empty(resolved.ModDirs); // своих папок нет — моды у агента на той машине
        Assert.Empty(resolved.Warnings); // и не пугаем предупреждениями: список модов окно возьмёт у агента
        // код в настройках — зашифрованный, не открытым текстом
        Assert.DoesNotContain(code.Key, Newtonsoft.Json.JsonConvert.SerializeObject(profile));
    }
}

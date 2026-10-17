using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using eViSTool.Core.Hosting;
using eViSTool.Hosting;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using Renci.SshNet.Sftp;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// Адаптер SSH.NET без настоящего сервера SFTP: перевод исключений SSH.NET в <see cref="RemoteFailure"/>, секреты не
/// уходят в тексты, файл ключа (нет, не ключ, зашифрован, неверный пароль ключа) разбирается до входа на сервер,
/// отпечатки ключа сервера сравниваются как у ssh. Сеть — только своя: порт на 127.0.0.1, который отвечает не по SSH.
/// Полный цикл на настоящем сервере — <see cref="LiveSftpTests"/>.
/// </summary>
public sealed class SshNetFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("evis-sftp-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* временная папка */ }
    }

    private static HostedEndpoint Endpoint(int port = 22, string user = "gp_user", string password = "", string? keyFile = null,
        string? passphrase = null) =>
        new(HostedProtocol.Sftp, "127.0.0.1", port, FtpTls.None, user, password, false, null)
        {
            KeyFile = keyFile, KeyPassphrase = passphrase, Timeout = TimeSpan.FromSeconds(5),
        };

    private static RemoteFailure Kind(Exception ex) => SshNetFiles.TranslateStatic(ex, "op /x").Kind;

    // ---------- перевод исключений

    [Fact]
    public void Translate_SftpStatus_MapsToFailures()
    {
        Assert.Equal(RemoteFailure.NotFound, Kind(new SftpPathNotFoundException("No such file")));
        Assert.Equal(RemoteFailure.Denied, Kind(new SftpPermissionDeniedException("Permission denied")));
        Assert.Equal(RemoteFailure.NotFound, Kind(new SftpException(StatusCode.NoSuchFile, "No such file")));
        Assert.Equal(RemoteFailure.Denied, Kind(new SftpException(StatusCode.PermissionDenied, "Permission denied")));
        Assert.Equal(RemoteFailure.NotFound, Kind(new SftpException((StatusCode)10, "No such path")));        // v4+
        Assert.Equal(RemoteFailure.Exists, Kind(new SftpException((StatusCode)11, "File already exists")));   // v4+
        Assert.Equal(RemoteFailure.Denied, Kind(new SftpException((StatusCode)12, "Write protected")));       // v4+
        Assert.Equal(RemoteFailure.Quota, Kind(new SftpException((StatusCode)14, "No space on filesystem"))); // v5+
        Assert.Equal(RemoteFailure.Quota, Kind(new SftpException((StatusCode)15, "Quota exceeded")));         // v5+
        Assert.Equal(RemoteFailure.Connect, Kind(new SftpException(StatusCode.ConnectionLost, "Connection lost")));
        // у версии 3 «нет места» — общий Failure, понятный только по тексту
        Assert.Equal(RemoteFailure.Quota, Kind(new SftpException(StatusCode.Failure, "No space left on device")));
        Assert.Equal(RemoteFailure.Quota, Kind(new SftpException(StatusCode.Failure, "Disk quota exceeded")));
        Assert.Equal(RemoteFailure.Protocol, Kind(new SftpException(StatusCode.Failure, "Failure")));
        Assert.Equal(RemoteFailure.Protocol, Kind(new SftpException(StatusCode.OperationUnsupported, "Operation unsupported")));
    }

    [Fact]
    public void Translate_SessionErrors_MapToFailures()
    {
        Assert.Equal(RemoteFailure.Auth, Kind(new SshAuthenticationException("Permission denied (password).")));
        Assert.Equal(RemoteFailure.Auth, Kind(new SshConnectionException("No more methods", DisconnectReason.NoMoreAuthenticationMethodsAvailable)));
        Assert.Equal(RemoteFailure.Auth, Kind(new SshConnectionException("Illegal user", DisconnectReason.IllegalUserName)));
        Assert.Equal(RemoteFailure.Connect, Kind(new SshConnectionException("An established connection was aborted by the server.", DisconnectReason.ConnectionLost)));
        Assert.Equal(RemoteFailure.Connect, Kind(new SshConnectionException("Client not connected.")));
        // на порту не SSH (например, FTP) или нет общих алгоритмов — не обрыв связи
        Assert.Equal(RemoteFailure.Protocol, Kind(new SshConnectionException("Server response does not contain SSH protocol identification.",
            DisconnectReason.ProtocolVersionNotSupported)));
        Assert.Equal(RemoteFailure.Protocol, Kind(new SshConnectionException("Key exchange negotiation failed.", DisconnectReason.KeyExchangeFailed)));
        Assert.Equal(RemoteFailure.Timeout, Kind(new SshOperationTimeoutException("Session operation has timed out")));
        Assert.Equal(RemoteFailure.Timeout, Kind(new TimeoutException()));
        Assert.Equal(RemoteFailure.Timeout, Kind(new SocketException((int)SocketError.TimedOut)));
        Assert.Equal(RemoteFailure.Connect, Kind(new SocketException((int)SocketError.ConnectionRefused)));
        Assert.Equal(RemoteFailure.Connect, Kind(new SocketException((int)SocketError.HostNotFound)));
        Assert.Equal(RemoteFailure.Connect, Kind(new ProxyException("proxy")));
        Assert.Equal(RemoteFailure.Connect, Kind(new ObjectDisposedException("Session")));
        Assert.Equal(RemoteFailure.Connect, Kind(new IOException("Unable to read data from the transport connection")));
        Assert.Equal(RemoteFailure.KeyPassphrase, Kind(new SshPassPhraseNullOrEmptyException("Private key is encrypted but passphrase is empty.")));
        Assert.Equal(RemoteFailure.Protocol, Kind(new SshException("Something odd")));
        Assert.Equal(RemoteFailure.Protocol, Kind(new InvalidOperationException("odd")));
    }

    [Fact]
    public void Translate_LooksInsideWrappers_AndKeepsOurOwn()
    {
        Assert.Equal(RemoteFailure.NotFound, Kind(new AggregateException(new SftpPathNotFoundException("No such file"))));
        Assert.Equal(RemoteFailure.Auth, Kind(new InvalidOperationException("wrapped", new SshAuthenticationException("denied"))));
        var own = new RemoteFilesException(RemoteFailure.Exists, "target exists");
        Assert.Same(own, SshNetFiles.TranslateStatic(own, "rename /a"));
        var translated = SshNetFiles.TranslateStatic(new SftpPathNotFoundException("No such file"), "read /Mods/a.zip");
        Assert.Contains("/Mods/a.zip", translated.Message);
        Assert.IsType<SftpPathNotFoundException>(translated.InnerException);
    }

    [Fact]
    public void Translate_NeverCarriesThePasswordOrThePassphrase()
    {
        const string password = "глубоко-спрятанный-42";
        const string passphrase = "фраза-ключа-17";
        var files = new SshNetFiles(Endpoint(password: password, passphrase: passphrase, keyFile: "/nowhere"));

        var inMessage = files.Translate(new SshAuthenticationException("server said " + password), "connect", null);
        Assert.Equal(RemoteFailure.Auth, inMessage.Kind);
        Assert.DoesNotContain(password, inMessage.ToString());
        Assert.Null(inMessage.InnerException); // цепочку с секретом не прикладываем

        var deep = files.Translate(new IOException("reset", new InvalidOperationException("wrapped", new Exception("key " + passphrase))), "list", "/Mods");
        Assert.Equal(RemoteFailure.Connect, deep.Kind);
        Assert.DoesNotContain(passphrase, deep.ToString());
    }

    // ---------- отпечатки ключа сервера

    [Theory]
    [InlineData("SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og", "SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og", true)]
    [InlineData("ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og", "SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og", true)]
    [InlineData(" sha256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og= ", "SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og", true)]
    [InlineData("SHA256:OHD8VZEXGWO6EZ8GSEJQ9WPAFGLFSOFLOTGGQCQO6OG", "SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og", false)] // base64 — с учётом регистра
    [InlineData("SHA256:AAAA", "SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og", false)]
    public void Fingerprints_CompareLikeSsh(string a, string b, bool same) => Assert.Equal(same, SshNetFiles.SameFingerprint(a, b));

    // ---------- файл ключа

    /// <summary>Закрытый ключ OpenSSH (openssh-key-v1) с нужным шифром — сам ключ внутри не настоящий.</summary>
    private static string OpenSshKey(string cipher)
    {
        static void Put(MemoryStream s, byte[] bytes)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            s.Write(length);
            s.Write(bytes);
        }
        using var blob = new MemoryStream();
        blob.Write("openssh-key-v1\0"u8);
        Put(blob, Encoding.ASCII.GetBytes(cipher));
        Put(blob, Encoding.ASCII.GetBytes(cipher == "none" ? "none" : "bcrypt"));
        Put(blob, cipher == "none" ? [] : [0, 0, 0, 16, .. RandomNumberGenerator.GetBytes(16), 0, 0, 0, 16]);
        blob.Write([0, 0, 0, 1]);
        Put(blob, [0, 0, 0, 11, .. "ssh-ed25519"u8, 0, 0, 0, 32, .. RandomNumberGenerator.GetBytes(32)]);
        Put(blob, RandomNumberGenerator.GetBytes(160));
        var body = Convert.ToBase64String(blob.ToArray(), Base64FormattingOptions.InsertLineBreaks);
        // заголовок собран из частей: в исходниках не должно быть строки, похожей на настоящий закрытый ключ
        return "-----BEGIN " + "OPENSSH PRIVATE KEY-----\n" + body + "\n-----END " + "OPENSSH PRIVATE KEY-----\n";
    }

    [Fact]
    public void KeyLooksEncrypted_ReadsTheKeyFormats()
    {
        static bool Encrypted(string text) => SshNetFiles.KeyLooksEncrypted(Encoding.ASCII.GetBytes(text));
        Assert.True(Encrypted(OpenSshKey("aes256-ctr")));
        Assert.False(Encrypted(OpenSshKey("none")));
        Assert.True(Encrypted("-----BEGIN " + "RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC,00\n\nAAAA\n-----END RSA PRIVATE KEY-----"));
        Assert.True(Encrypted("-----BEGIN " + "ENCRYPTED PRIVATE KEY-----\nAAAA\n-----END ENCRYPTED PRIVATE KEY-----"));
        Assert.False(Encrypted("-----BEGIN " + "RSA PRIVATE KEY-----\nAAAA\n-----END RSA PRIVATE KEY-----"));
        Assert.True(Encrypted("PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: aes256-cbc\nComment: x\n"));
        Assert.False(Encrypted("PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: none\nComment: x\n"));
        Assert.False(Encrypted("-----BEGIN " + "OPENSSH PRIVATE KEY-----\n!!! not base64 !!!\n-----END OPENSSH PRIVATE KEY-----"));
        Assert.False(Encrypted("hello"));
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Подключиться и вернуть, чем кончилось. Ключ читается до сети: беда с ним видна раньше, чем ответ порта.</summary>
    private static async Task<RemoteFilesException> FailAsync(HostedEndpoint endpoint)
    {
        await using var files = new SshNetFiles(endpoint);
        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => files.ConnectAsync(CancellationToken.None));
        Assert.False(files.IsConnected);
        return ex;
    }

    [Fact]
    public async Task KeyFile_Missing_IsKeyFile_BeforeAnyNetwork()
    {
        var missing = Path.Combine(_dir, "no_such_key");
        var ex = await FailAsync(Endpoint(port: 1, keyFile: missing));
        Assert.Equal(RemoteFailure.KeyFile, ex.Kind);
        Assert.Contains(missing, ex.Message);
        HostedTestText.AssertIs("hosted.errKeyFile", HostedSecret.Describe(ex).Replace(ex.Message, "{0}"));
    }

    [Fact]
    public async Task KeyFile_NotAKey_IsKeyFile()
    {
        var ex = await FailAsync(Endpoint(port: 1, keyFile: Write("notes.txt", "just some text")));
        Assert.Equal(RemoteFailure.KeyFile, ex.Kind);
    }

    [Fact]
    public async Task KeyFile_EncryptedWithoutPassphrase_IsKeyPassphrase()
    {
        var ex = await FailAsync(Endpoint(port: 1, keyFile: Write("id_enc", OpenSshKey("aes256-ctr"))));
        Assert.Equal(RemoteFailure.KeyPassphrase, ex.Kind);
        HostedTestText.AssertIs("hosted.errKeyPassphrase", HostedSecret.Describe(ex));
    }

    [Fact]
    public async Task KeyFile_RealRsaKeys_OpenWithTheRightPassphraseOnly()
    {
        using var rsa = RSA.Create(2048);
        var plain = Write("id_rsa", rsa.ExportRSAPrivateKeyPem());
        var encrypted = Write("id_rsa_enc", rsa.ExportEncryptedPkcs8PrivateKeyPem("made-up-passphrase",
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000)));
        await using var server = new NotSshServer();

        // ключ открылся — дело дошло до сервера, а тот отвечает не по SSH и закрывает соединение
        Assert.Equal(RemoteFailure.Connect, (await FailAsync(Endpoint(server.Port, keyFile: plain))).Kind);
        Assert.Equal(RemoteFailure.Connect, (await FailAsync(Endpoint(server.Port, keyFile: encrypted, passphrase: "made-up-passphrase"))).Kind);

        Assert.Equal(RemoteFailure.KeyPassphrase, (await FailAsync(Endpoint(server.Port, keyFile: encrypted))).Kind);
        var wrong = await FailAsync(Endpoint(server.Port, keyFile: encrypted, passphrase: "not-this-one"));
        Assert.Equal(RemoteFailure.KeyPassphrase, wrong.Kind);
        Assert.DoesNotContain("not-this-one", wrong.ToString());
    }

    [Fact]
    public async Task KeyFile_HomeTilde_IsTheUserFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".ssh/id_ed25519"), HostedEndpoint.ExpandKeyPath("~/.ssh/id_ed25519"));
        Assert.Equal(Path.Combine(home, @".ssh\id_ed25519"), HostedEndpoint.ExpandKeyPath(@"~\.ssh\id_ed25519"));
        Assert.Equal("/etc/key", HostedEndpoint.ExpandKeyPath("  /etc/key "));
        Assert.Equal("~other/key", HostedEndpoint.ExpandKeyPath("~other/key")); // чужая домашняя папка — как есть
        var ex = await FailAsync(Endpoint(port: 1, keyFile: "~/evistool-no-such-key-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(RemoteFailure.KeyFile, ex.Kind);
        Assert.Contains(home, ex.Message);
    }

    // ---------- подключение без настоящего сервера

    [Fact]
    public async Task EmptyLogin_IsAuth_WithoutConnecting()
    {
        var ex = await FailAsync(Endpoint(port: 1, user: " "));
        Assert.Equal(RemoteFailure.Auth, ex.Kind);
    }

    [Fact]
    public async Task NotAnSshServer_IsAConnectionFailure_NotALoginProblem()
    {
        // SSH.NET строки до «SSH-2.0-…» пропускает (так можно по RFC 4253), поэтому FTP на порту SFTP — это обрыв или
        // тишина, а не «неверный пароль»
        await using var server = new NotSshServer();
        var ex = await FailAsync(Endpoint(server.Port, password: "made-up-password"));
        Assert.Equal(RemoteFailure.Connect, ex.Kind);
        Assert.DoesNotContain("made-up-password", ex.ToString());
        Assert.False(ex.IsHostKey);
    }

    [Fact]
    public async Task ClosedPort_IsConnect()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var ex = await FailAsync(Endpoint(port, password: "made-up-password"));
        Assert.True(ex.Kind is RemoteFailure.Connect or RemoteFailure.Timeout, ex.Kind.ToString());
    }

    [Fact]
    public void Features_AreAlwaysEncryptedWithRangeReads()
    {
        var files = new SshNetFiles(Endpoint());
        Assert.True(files.Features.Encrypted);
        Assert.True(files.Features.RangeReads);
        Assert.False(files.IsConnected);
    }

    // ---------- регистрация и границы сборок

    [Fact]
    public void Register_MakesSftpAvailableThroughFactory()
    {
        HostedTransports.Register();
        Assert.True(RemoteFilesFactory.Supports(HostedProtocol.Sftp));
        var endpoint = new HostedEndpoint(HostedProtocol.Sftp, "sftp-" + Guid.NewGuid().ToString("N") + ".test", 22, FtpTls.None, "u", "p", false, null);
        Assert.IsType<SshNetFiles>(RemoteFilesFactory.Create(endpoint));
    }

    [Fact]
    public void SshNet_StaysOutOfAgentAndCore()
    {
        // агент публикуется обрезанным одним exe — SSH.NET и BouncyCastle ему не нужны; Core про SSH.NET не знает
        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        var agent = File.ReadAllText(Path.Combine(src, "eViSTool.Agent", "eViSTool.Agent.csproj"));
        var core = File.ReadAllText(Path.Combine(src, "eViSTool.Core", "eViSTool.Core.csproj"));
        foreach (var csproj in new[] { agent, core })
        {
            Assert.DoesNotContain("eViSTool.Hosting", csproj);
            Assert.DoesNotContain("SSH.NET", csproj);
            Assert.DoesNotContain("BouncyCastle", csproj);
        }
        var coreRefs = typeof(RemoteFilesFactory).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.DoesNotContain(typeof(SftpException).Assembly.GetName().Name, coreRefs);
        Assert.DoesNotContain(typeof(SshNetFiles).Assembly.GetName().Name, coreRefs);
    }

    /// <summary>Порт на 127.0.0.1, где отвечает не SSH: приветствие FTP и закрытое соединение.</summary>
    private sealed class NotSshServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public NotSshServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                    using (client)
                    {
                        try { await client.GetStream().WriteAsync("220 welcome\r\n"u8.ToArray(), _stop.Token); }
                        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { /* клиент ушёл */ }
                    }
                }
            });
        }

        public int Port { get; }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try { await _loop; } catch (OperationCanceledException) { /* остановлен */ }
            _stop.Dispose();
        }
    }
}

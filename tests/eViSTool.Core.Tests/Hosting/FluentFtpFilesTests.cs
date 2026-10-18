using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using eViSTool.Core.Hosting;
using eViSTool.Hosting;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// Адаптер FluentFTP на своём <see cref="MiniFtpServer"/> в двух режимах: «как G-Portal» (FEAT/NOOP/MDTM — 502,
/// MLSD без объявления, поверх не переименовывает, после обрыва передачи молчит, странный текст PASV) и «как vsftpd».
/// </summary>
public sealed class FluentFtpFilesTests
{
    public static TheoryData<string> Modes => ["gportal", "vsftpd"];

    private static MiniFtpOptions Options(string mode) => mode == "gportal" ? MiniFtpOptions.GPortal() : MiniFtpOptions.Vsftpd();

    private static HostedEndpoint Endpoint(MiniFtpServer server, string? password = null, FtpTls tls = FtpTls.None,
        string? trusted = null, double timeoutSeconds = 5) =>
        new(HostedProtocol.Ftp, "127.0.0.1", server.Port, tls, server.Options.User, password ?? server.Options.Password, false, trusted)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };

    private static async Task<FluentFtpFiles> ConnectAsync(MiniFtpServer server, HostedEndpoint? endpoint = null)
    {
        var files = new FluentFtpFiles(endpoint ?? Endpoint(server));
        await files.ConnectAsync(CancellationToken.None);
        return files;
    }

    private static byte[] Random(int size, int seed = 1)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static readonly CancellationToken None = CancellationToken.None;

    // ---------- подключение и возможности

    [Theory, MemberData(nameof(Modes))]
    public async Task Connect_WorksWithoutFeatAndNoop(string mode)
    {
        await using var server = new MiniFtpServer(Options(mode));
        server.Put("/Mods/a.zip", [1, 2, 3]);
        await using var files = await ConnectAsync(server);

        Assert.True(files.IsConnected);
        Assert.Equal(mode == "gportal" ? "220 welcome" : "220 (vsFTPd 3.0.5)", files.Features.Banner);
        Assert.True(files.Features.RangeReads);
        Assert.False(files.Features.Encrypted);
        Assert.Single(await files.ListAsync("/Mods", None));
        // FEAT — одна проба (G-Portal ответил 502, это не ошибка); NOOP и MDTM не шлются никогда
        Assert.Equal(1, server.Count("FEAT"));
        Assert.Equal(0, server.Count("NOOP"));
        Assert.Equal(0, server.Count("MDTM"));
        Assert.Equal(mode == "vsftpd" ? 1 : 0, server.Count("OPTS")); // OPTS UTF8 — только когда UTF8 объявлен
    }

    [Fact]
    public async Task IdleConnection_IsCheckedWithPwd_NotNoop()
    {
        await using var server = new MiniFtpServer(MiniFtpOptions.GPortal());
        server.Put("/x.txt", [1]);
        await using var files = new FluentFtpFiles(Endpoint(server)) { IdleCheck = TimeSpan.Zero };
        await files.ConnectAsync(None);
        Assert.NotNull(await files.StatAsync("/x.txt", None));
        Assert.NotNull(await files.StatAsync("/x.txt", None));
        Assert.True(server.Count("PWD") >= 2);
        Assert.Equal(0, server.Count("NOOP"));
        Assert.Equal(1, server.Sessions);
    }

    [Fact]
    public async Task DeadIdleConnection_IsReplacedBeforeOperation()
    {
        var options = MiniFtpOptions.GPortal();
        await using var server = new MiniFtpServer(options);
        server.Put("/x.txt", [1]);
        await using var files = new FluentFtpFiles(Endpoint(server, timeoutSeconds: 2)) { IdleCheck = TimeSpan.Zero };
        await files.ConnectAsync(None);
        options.Fail("PWD", null, "421 Timeout.", once: true); // сервер закрыл сеанс за простой
        Assert.NotNull(await files.StatAsync("/x.txt", None));
        Assert.Equal(2, server.Sessions);
    }

    // ---------- листинг

    [Theory, MemberData(nameof(Modes))]
    public async Task List_ReturnsFilesAndFolders_WithSizesAndTimes(string mode)
    {
        await using var server = new MiniFtpServer(Options(mode));
        var when = new DateTime(2026, 10, 17, 8, 23, 33, DateTimeKind.Utc);
        server.Put("/Mods/Auto_Map_Markers_5.0.3_-_Vintage_Story_1.22.zip", Random(2000), when);
        server.Put("/Mods/мод с пробелом.zip", Random(10), when);
        Directory.CreateDirectory(server.Local("/Mods/sub"));
        await using var files = await ConnectAsync(server);

        var list = (await files.ListAsync("/Mods", None)).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(["Auto_Map_Markers_5.0.3_-_Vintage_Story_1.22.zip", "sub", "мод с пробелом.zip"], list.Select(e => e.Name));
        Assert.Equal("/Mods/Auto_Map_Markers_5.0.3_-_Vintage_Story_1.22.zip", list[0].Path);
        Assert.Equal(2000, list[0].Size);
        Assert.False(list[0].IsDirectory);
        Assert.True(list[1].IsDirectory);
        Assert.Equal("/Mods/мод с пробелом.zip", list[2].Path);
        if (mode == "gportal")
        {
            // MLSD без объявления в FEAT: время UTC до секунды
            Assert.Equal(when, list[0].Modified);
            Assert.Equal(DateTimeKind.Utc, list[0].Modified!.Value.Kind);
            Assert.Equal(0, server.Count("LIST"));
        }
        else
        {
            // vsftpd: MLSD «500 Unknown command» → LIST, время до минуты
            Assert.Equal(new DateTime(2026, 10, 17, 8, 23, 0), list[0].Modified);
            await files.ListAsync("/Mods", None);
            Assert.Equal(1, server.Count("MLSD")); // отказ запомнен, второй раз MLSD не пробуется
        }
    }

    [Fact]
    public async Task List_ParsesRealGPortalListLines()
    {
        // MLSD у G-Portal работает, но если откажет — LIST в настоящем формате хостера (имена с пробелами и дефисами)
        var options = MiniFtpOptions.GPortal();
        options.Mlsd = false;
        options.RawList["/Mods"] = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Hosting", "gportal-list-mods.txt"));
        await using var server = new MiniFtpServer(options);
        Directory.CreateDirectory(server.Local("/Mods"));
        await using var files = await ConnectAsync(server);

        var list = await files.ListAsync("/Mods", None);
        Assert.Equal(10, list.Count);
        var markers = Assert.Single(list, e => e.Name == "Auto_Map_Markers_5.0.3_-_Vintage_Story_1.22.zip");
        Assert.Equal(2001828, markers.Size);
        Assert.Equal("/Mods/Auto_Map_Markers_5.0.3_-_Vintage_Story_1.22.zip", markers.Path);
        Assert.Equal((10, 23), (markers.Modified!.Value.Hour, markers.Modified.Value.Minute));
        Assert.Contains(list, e => e.Name == "do_not_add_mods_here.txt" && e.Size == 189);
        Assert.All(list, e => Assert.False(e.IsDirectory));
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task List_MissingFolder_IsNotFound(string mode)
    {
        await using var server = new MiniFtpServer(Options(mode));
        Directory.CreateDirectory(server.Local("/empty"));
        await using var files = await ConnectAsync(server);
        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => files.ListAsync("/nope", None));
        Assert.Equal(RemoteFailure.NotFound, ex.Kind);
        Assert.Empty(await files.ListAsync("/empty", None)); // пустая папка — не ошибка
        Assert.True(Assert.Single(await files.ListAsync("/", None)).IsDirectory);
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task Stat_FindsFilesAndFolders_NullForMissing(string mode)
    {
        await using var server = new MiniFtpServer(Options(mode));
        server.Put("/Mods/a.zip", Random(77));
        await using var files = await ConnectAsync(server);

        var file = await files.StatAsync("/Mods/a.zip", None);
        Assert.NotNull(file);
        Assert.Equal(77, file.Size);
        Assert.False(file.IsDirectory);
        Assert.True((await files.StatAsync("/Mods", None))!.IsDirectory);
        Assert.True((await files.StatAsync("/", None))!.IsDirectory);
        Assert.Null(await files.StatAsync("/Mods/b.zip", None));
        Assert.Null(await files.StatAsync("/nope/b.zip", None));
        Assert.Equal(0, server.Count("MDTM"));
    }

    // ---------- чтение с позиции

    [Theory, MemberData(nameof(Modes))]
    public async Task ReadRange_Tail_ReadsToEnd_AndKeepsConnection(string mode)
    {
        await using var server = new MiniFtpServer(Options(mode));
        var data = Random(300_000);
        server.Put("/Mods/big.zip", data);
        await using var files = await ConnectAsync(server);

        var tail = await files.ReadRangeAsync("/Mods/big.zip", data.Length - 1000, 65_557, None);
        Assert.Equal(data[^1000..], tail); // короче запрошенного — дошли до конца файла
        var exact = await files.ReadRangeAsync("/Mods/big.zip", data.Length - 500, 500, None);
        Assert.Equal(data[^500..], exact);
        Assert.True(files.IsConnected);
        Assert.NotEmpty(await files.ListAsync("/Mods", None));
        Assert.Equal(1, server.Sessions); // ни одного переподключения
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task ReadRange_Middle_AbortsTransfer_AndNextCommandWorks(string mode)
    {
        await using var server = new MiniFtpServer(Options(mode));
        var data = Random(4 * 1024 * 1024);
        server.Put("/Mods/big.zip", data);
        await using var files = await ConnectAsync(server, Endpoint(server, timeoutSeconds: 10));

        var watch = Stopwatch.StartNew();
        var middle = await files.ReadRangeAsync("/Mods/big.zip", 1024, 1000, None);
        Assert.Equal(data.AsSpan(1024, 1000).ToArray(), middle);
        // G-Portal после обрыва молчит: ответа не ждём, сразу бросаем соединение
        Assert.False(files.IsConnected);
        var again = await files.ReadRangeAsync("/Mods/big.zip", 2_000_000, 100, None);
        Assert.Equal(data.AsSpan(2_000_000, 100).ToArray(), again);
        Assert.Single(await files.ListAsync("/Mods", None));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"обрыв ждал ответа сервера: {watch.Elapsed}");
        Assert.Equal(3, server.Sessions);
    }

    [Fact]
    public async Task ReadRange_RestRefused_IsRangeUnsupported_WholeReadStillWorks()
    {
        var options = MiniFtpOptions.GPortal();
        options.Rest = false;
        await using var server = new MiniFtpServer(options);
        var data = Random(50_000);
        server.Put("/a.zip", data);
        await using var files = await ConnectAsync(server);

        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => files.ReadRangeAsync("/a.zip", 100, 10, None));
        Assert.Equal(RemoteFailure.RangeUnsupported, ex.Kind);
        Assert.False(files.Features.RangeReads);
        var restCount = server.Count("REST");
        ex = await Assert.ThrowsAsync<RemoteFilesException>(() => files.ReadRangeAsync("/a.zip", 100, 10, None));
        Assert.Equal(RemoteFailure.RangeUnsupported, ex.Kind);
        Assert.Equal(restCount, server.Count("REST")); // запомнено, сервер больше не спрашиваем
        Assert.Equal(data, await files.ReadRangeAsync("/a.zip", 0, data.Length + 10, None));
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task ReadRange_MissingFile_IsNotFound(string mode)
    {
        await using var server = new MiniFtpServer(Options(mode));
        Directory.CreateDirectory(server.Local("/Mods"));
        await using var files = await ConnectAsync(server);
        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => files.ReadRangeAsync("/Mods/none.zip", 10, 10, None));
        Assert.Equal(RemoteFailure.NotFound, ex.Kind);
    }

    // ---------- скачивание и заливка

    [Theory, MemberData(nameof(Modes))]
    public async Task DownloadAndUpload_AreByteForByte(string mode)
    {
        await using var server = new MiniFtpServer(Options(mode));
        var data = Encoding.UTF8.GetBytes("{\r\n  \"ModPaths\": [\"Mods\"]\r\n}\n\r\n").Concat(Random(100_000)).ToArray();
        server.Put("/serverconfig.json", data);
        await using var files = await ConnectAsync(server);

        using var got = new MemoryStream();
        long reported = 0;
        await files.DownloadAsync("/serverconfig.json", got, new SyncProgress(n => reported = n), None);
        Assert.Equal(data, got.ToArray());

        var upload = Random(70_000, seed: 2).Concat("\r\n\n"u8.ToArray()).ToArray();
        await files.UploadAsync(new MemoryStream(upload), "/новый мод.zip", null, None);
        Assert.Equal(upload, await File.ReadAllBytesAsync(server.Local("/новый мод.zip")));
        // поверх существующего — перезапись
        await files.UploadAsync(new MemoryStream([9, 9]), "/serverconfig.json", null, None);
        Assert.Equal([9, 9], await File.ReadAllBytesAsync(server.Local("/serverconfig.json")));
        Assert.True(reported > 0);

        await files.WriteTextAsync("/text.json", "{ \"Имя\": 1 }\r\n", None);
        Assert.Equal("{ \"Имя\": 1 }\r\n", await files.ReadTextAsync("/text.json", None));
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task Download_MissingFile_IsNotFound(string mode)
    {
        await using var server = new MiniFtpServer(Options(mode));
        await using var files = await ConnectAsync(server);
        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => files.DownloadAsync("/none.json", new MemoryStream(), null, None));
        Assert.Equal(RemoteFailure.NotFound, ex.Kind);
    }

    [Fact]
    public async Task CyrillicNames_SurviveWithoutOptsUtf8()
    {
        await using var server = new MiniFtpServer(MiniFtpOptions.GPortal());
        await using var files = await ConnectAsync(server);
        await files.CreateDirectoryAsync("/Моды", None);
        await files.UploadAsync(new MemoryStream([1, 2]), "/Моды/ёжик.zip", null, None);
        Assert.True(File.Exists(server.Local("/Моды/ёжик.zip")));
        Assert.Equal("ёжик.zip", Assert.Single(await files.ListAsync("/Моды", None)).Name);
        Assert.Equal(0, server.Count("OPTS"));
    }

    // ---------- переименование, удаление, папки

    [Fact]
    public async Task Rename_OverExisting_GPortal_IsExists_AndNothingChanges()
    {
        await using var server = new MiniFtpServer(MiniFtpOptions.GPortal());
        server.Put("/serverconfig.json", [1]);
        server.Put("/serverconfig.json.new", [2]);
        await using var files = await ConnectAsync(server);

        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => files.RenameAsync("/serverconfig.json.new", "/serverconfig.json", None));
        Assert.Equal(RemoteFailure.Exists, ex.Kind);
        Assert.Equal([1], await File.ReadAllBytesAsync(server.Local("/serverconfig.json")));
        Assert.Equal([2], await File.ReadAllBytesAsync(server.Local("/serverconfig.json.new")));

        // путь «удалить старое, потом переименовать»
        await files.DeleteFileAsync("/serverconfig.json", None);
        await files.RenameAsync("/serverconfig.json.new", "/serverconfig.json", None);
        Assert.Equal([2], await File.ReadAllBytesAsync(server.Local("/serverconfig.json")));
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task Rename_ToNewName_Works_MissingSource_IsNotFound(string mode)
    {
        await using var server = new MiniFtpServer(Options(mode));
        server.Put("/Mods/a.zip", [1]);
        await using var files = await ConnectAsync(server);

        await files.RenameAsync("/Mods/a.zip", "/Mods/a.zip.disabled", None);
        Assert.True(File.Exists(server.Local("/Mods/a.zip.disabled")));
        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => files.RenameAsync("/Mods/a.zip", "/Mods/b.zip", None));
        Assert.Equal(RemoteFailure.NotFound, ex.Kind);
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task Delete_And_CreateDirectory(string mode)
    {
        await using var server = new MiniFtpServer(Options(mode));
        server.Put("/Mods/a.zip", [1]);
        await using var files = await ConnectAsync(server);

        await files.DeleteFileAsync("/Mods/a.zip", None);
        Assert.False(File.Exists(server.Local("/Mods/a.zip")));
        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => files.DeleteFileAsync("/Mods/a.zip", None));
        Assert.Equal(RemoteFailure.NotFound, ex.Kind);

        await files.CreateDirectoryAsync("/a/b/c", None); // недостающие родители создаются
        Assert.True(Directory.Exists(server.Local("/a/b/c")));
        await files.CreateDirectoryAsync("/a/b/c", None); // уже есть — не ошибка
        await files.CreateDirectoryAsync("/Mods", None);
    }

    [Fact]
    public async Task Delete_Denied_IsDenied()
    {
        var options = MiniFtpOptions.GPortal().Fail("DELE", "keep.zip", "550 Permission denied.");
        await using var server = new MiniFtpServer(options);
        server.Put("/Mods/keep.zip", [1]);
        await using var files = await ConnectAsync(server);
        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => files.DeleteFileAsync("/Mods/keep.zip", None));
        Assert.Equal(RemoteFailure.Denied, ex.Kind); // 550, но файл есть — значит нельзя, а не нет
    }

    [Fact]
    public async Task Upload_QuotaExceeded_IsQuota()
    {
        var options = MiniFtpOptions.GPortal().Fail("STOR", null, "552 Disk quota exceeded.");
        await using var server = new MiniFtpServer(options);
        await using var files = await ConnectAsync(server);
        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => files.UploadAsync(new MemoryStream([1]), "/a.zip", null, None));
        Assert.Equal(RemoteFailure.Quota, ex.Kind);
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task Upload_CancelledMidway_DropsTheConnection_TheNextCommandWorks(string mode)
    {
        await using var server = new MiniFtpServer(Options(mode));
        server.Put("/Mods/a.zip", [1]);
        await using var files = await ConnectAsync(server);
        using var cts = new CancellationTokenSource();
        var source = new CancelOnRead(new MemoryStream(Random(4 * 1024 * 1024)), 256 * 1024, cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.UploadAsync(source, "/Mods/big.zip", null, cts.Token));

        // в соединение с недоделанной передачей больше ничего не шлём: у G-Portal оно мёртвое, и уборка ушла бы в никуда
        Assert.False(files.IsConnected);
        Assert.Contains(await files.ListAsync("/Mods", None), e => e.Name == "a.zip");
    }

    /// <summary>Источник заливки, который отменяет её, когда из него прочитали столько-то байт.</summary>
    private sealed class CancelOnRead(Stream inner, long after, CancellationTokenSource cts) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (inner.Position >= after) cts.Cancel();
            return inner.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (inner.Position >= after) cts.Cancel();
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---------- пассивный режим

    [Fact]
    public async Task Pasv_WithUnreachableAddress_RetriesWithControlAddress()
    {
        var options = MiniFtpOptions.GPortal();
        options.NotImplemented.Add("EPSV");
        options.PassiveIp = "127.0.0.2"; // слушаем только 127.0.0.1 — по названному адресу не достучаться
        await using var server = new MiniFtpServer(options);
        server.Put("/Mods/a.zip", [1, 2, 3]);
        await using var files = await ConnectAsync(server, Endpoint(server, timeoutSeconds: 3));

        Assert.Single(await files.ListAsync("/Mods", None));
        Assert.Equal([1, 2, 3], await files.ReadRangeAsync("/Mods/a.zip", 0, 100, None));
        Assert.True(server.Count("PASV") >= 2);
    }

    [Fact]
    public async Task ActiveMode_Works()
    {
        await using var server = new MiniFtpServer(MiniFtpOptions.Vsftpd());
        server.Put("/Mods/a.zip", [1, 2, 3]);
        var endpoint = Endpoint(server) with { ActiveMode = true };
        await using var files = await ConnectAsync(server, endpoint);
        Assert.Single(await files.ListAsync("/Mods", None));
        Assert.Equal(0, server.Count("PASV") + server.Count("EPSV"));
    }

    // ---------- ошибки входа, сети и TLS

    [Theory, MemberData(nameof(Modes))]
    public async Task WrongPassword_IsAuth_AndPasswordNeverLeaks(string mode)
    {
        await using var server = new MiniFtpServer(Options(mode));
        const string wrong = "неверный-Пароль-42";
        var files = new FluentFtpFiles(Endpoint(server, password: wrong));
        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => files.ConnectAsync(None));
        Assert.Equal(RemoteFailure.Auth, ex.Kind);
        Assert.DoesNotContain(wrong, ex.ToString());
        Assert.DoesNotContain(wrong, Endpoint(server, password: wrong).ToString());
        Assert.False(files.IsConnected);
    }

    [Fact]
    public async Task NothingListening_IsConnect()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var endpoint = new HostedEndpoint(HostedProtocol.Ftp, "127.0.0.1", port, FtpTls.None, "u", "p", false, null) { Timeout = TimeSpan.FromSeconds(3) };
        await using var files = new FluentFtpFiles(endpoint);
        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => files.ConnectAsync(None));
        Assert.Equal(RemoteFailure.Connect, ex.Kind);
    }

    [Fact]
    public async Task SilentServer_IsTimeout()
    {
        var options = MiniFtpOptions.GPortal().Fail("PASS", null, null); // на пароль — ни слова
        await using var server = new MiniFtpServer(options);
        await using var files = new FluentFtpFiles(Endpoint(server, timeoutSeconds: 1.5));
        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => files.ConnectAsync(None));
        Assert.Equal(RemoteFailure.Timeout, ex.Kind);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancel_PassesThroughAsCancellation()
    {
        var options = MiniFtpOptions.GPortal().Fail("PASS", null, null);
        await using var server = new MiniFtpServer(options);
        await using var files = new FluentFtpFiles(Endpoint(server, timeoutSeconds: 20));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.ConnectAsync(cts.Token));
    }

    [Fact]
    public async Task Tls_TrustedFingerprint_Connects_UnknownIsCertificateUntrusted()
    {
        using var cert = MiniFtpServer.CreateCertificate();
        var options = MiniFtpOptions.Vsftpd();
        options.Certificate = cert;
        await using var server = new MiniFtpServer(options);
        server.Put("/Mods/a.zip", [1, 2, 3]);
        var sha = Convert.ToHexString(SHA256.HashData(cert.RawData));

        var stranger = new FluentFtpFiles(Endpoint(server, tls: FtpTls.Explicit));
        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => stranger.ConnectAsync(None));
        Assert.Equal(RemoteFailure.CertificateUntrusted, ex.Kind);
        Assert.Equal(sha, ex.CertificateSha256);
        Assert.Contains("miniftp.test", ex.CertificateSubject);

        // в Auto чужой сертификат тоже не повод молча уйти на открытый текст
        var auto = new FluentFtpFiles(Endpoint(server, tls: FtpTls.Auto));
        ex = await Assert.ThrowsAsync<RemoteFilesException>(() => auto.ConnectAsync(None));
        Assert.Equal(RemoteFailure.CertificateUntrusted, ex.Kind);

        var lower = string.Join(":", Enumerable.Range(0, sha.Length / 2).Select(i => sha.Substring(i * 2, 2))).ToLowerInvariant();
        await using var trusted = await ConnectAsync(server, Endpoint(server, tls: FtpTls.Explicit, trusted: lower));
        Assert.True(trusted.Features.Encrypted);
        Assert.Single(await trusted.ListAsync("/Mods", None));
        Assert.Equal([1, 2, 3], await trusted.ReadRangeAsync("/Mods/a.zip", 0, 10, None));
    }

    [Fact]
    public async Task Tls_Auto_WithoutTlsOnServer_FallsBackToPlain_ExplicitFails()
    {
        await using var server = new MiniFtpServer(MiniFtpOptions.GPortal()); // AUTH — 502
        await using var auto = await ConnectAsync(server, Endpoint(server, tls: FtpTls.Auto));
        Assert.False(auto.Features.Encrypted);
        var strict = new FluentFtpFiles(Endpoint(server, tls: FtpTls.Explicit));
        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => strict.ConnectAsync(None));
        Assert.Equal(RemoteFailure.Tls, ex.Kind);
    }

    // ---------- главное правило: наружу только RemoteFilesException

    public static TheoryData<string, string?> Failures()
    {
        var data = new TheoryData<string, string?>();
        foreach (var command in new[] { "MLSD", "LIST", "RETR", "STOR", "DELE", "RNFR", "RNTO", "MKD", "PASV", "EPSV", "REST", "TYPE" })
            foreach (var reply in new[] { "421 Service not available, closing control connection.", "425 Can't open data connection.",
                         "450 Busy.", "451 Local error.", "500 Syntax error.", "550 No.", "552 Quota.", "553 Bad name.", "999 Garbage" })
                data.Add(command, reply);
        // молчание — каждая операция ждёт тайм-аут, поэтому только на командах передачи и записи
        foreach (var command in new[] { "MLSD", "RETR", "STOR", "RNTO", "EPSV" }) data.Add(command, null);
        return data;
    }

    [Theory, MemberData(nameof(Failures))]
    public async Task AnyServerFailure_SurfacesOnlyAsRemoteFilesException(string command, string? reply)
    {
        var options = MiniFtpOptions.GPortal();
        await using var server = new MiniFtpServer(options);
        server.Put("/Mods/a.zip", Random(300_000));
        server.Put("/Mods/b.zip", [1]);
        await using var files = await ConnectAsync(server, Endpoint(server, timeoutSeconds: 1));
        options.Fail(command, null, reply);

        var operations = new Func<Task>[]
        {
            () => files.ListAsync("/Mods", None),
            () => files.StatAsync("/Mods/a.zip", None),
            () => files.ReadRangeAsync("/Mods/a.zip", 1000, 100, None),
            () => files.ReadRangeAsync("/Mods/a.zip", 299_000, 2000, None),
            () => files.DownloadAsync("/Mods/b.zip", new MemoryStream(), null, None),
            () => files.UploadAsync(new MemoryStream([1, 2]), "/Mods/c.zip", null, None),
            () => files.RenameAsync("/Mods/b.zip", "/Mods/b2.zip", None),
            () => files.DeleteFileAsync("/Mods/b2.zip", None),
            () => files.CreateDirectoryAsync("/Mods/new/dir", None),
        };
        foreach (var operation in operations)
        {
            try { await operation(); }
            catch (RemoteFilesException ex)
            {
                Assert.DoesNotContain(server.Options.Password, ex.ToString());
            }
            catch (Exception ex)
            {
                Assert.Fail($"{command} → «{reply}»: наружу вылетело {ex.GetType().FullName}: {ex.Message}");
            }
        }
    }

    [Fact]
    public async Task Pasv_RetryThatDidNotHelp_IsNotKept()
    {
        var options = MiniFtpOptions.GPortal().Fail("MLSD", null, null); // и по верному адресу передача не пойдёт
        options.NotImplemented.Add("EPSV");
        options.PassiveIp = "127.0.0.2";
        await using var server = new MiniFtpServer(options);
        await using var files = await ConnectAsync(server, Endpoint(server, timeoutSeconds: 1.5));

        await Assert.ThrowsAsync<RemoteFilesException>(() => files.ListAsync("/", None));
        Assert.False(files.PassiveUsesControlAddress);
    }

    [Fact]
    public async Task ControlTimeout_WithCorrectPasvAddress_DoesNotSwitchToControlAddress()
    {
        var options = MiniFtpOptions.GPortal().Fail("MLSD", null, null, once: true);
        options.NotImplemented.Add("EPSV"); // PASV называет 127.0.0.1 — тот же адрес, что у управляющего соединения
        await using var server = new MiniFtpServer(options);
        server.Put("/Mods/a.zip", [1]);
        await using var files = await ConnectAsync(server, Endpoint(server, timeoutSeconds: 1.5));

        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => files.ListAsync("/Mods", None));
        Assert.Equal(RemoteFailure.Timeout, ex.Kind);
        Assert.False(files.PassiveUsesControlAddress);
        Assert.Single(await files.ListAsync("/Mods", None));
    }

    [Fact]
    public async Task Download_ToNonSeekableStream_IsNotRetried()
    {
        var options = MiniFtpOptions.GPortal();
        options.NotImplemented.Add("EPSV");
        options.PassiveIp = "127.0.0.2";
        await using var server = new MiniFtpServer(options);
        server.Put("/a.zip", [1, 2, 3]);
        await using var files = await ConnectAsync(server, Endpoint(server, timeoutSeconds: 1.5));

        await Assert.ThrowsAsync<RemoteFilesException>(() => files.DownloadAsync("/a.zip", new ForwardOnlyStream(), null, None));
        Assert.False(files.PassiveUsesControlAddress);
        Assert.Equal(1, server.Count("PASV")); // передачу открывали один раз, без повтора
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task PathWithDotDot_IsRemoteFilesException(string mode)
    {
        await using var server = new MiniFtpServer(Options(mode));
        await using var files = await ConnectAsync(server);
        var operations = new Func<Task>[]
        {
            () => files.ListAsync("/Mods/../etc", None),
            () => files.StatAsync("/../a", None),
            () => files.ReadRangeAsync("/a/../b", 0, 1, None),
            () => files.DownloadAsync("/..", new MemoryStream(), null, None),
            () => files.UploadAsync(new MemoryStream([1]), "/../x", null, None),
            () => files.RenameAsync("/a", "/../b", None),
            () => files.DeleteFileAsync("/../a", None),
            () => files.CreateDirectoryAsync("/a/..", None),
        };
        foreach (var operation in operations)
            Assert.Equal(RemoteFailure.Denied, (await Assert.ThrowsAsync<RemoteFilesException>(operation)).Kind);
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task Rename_MissingSource_OccupiedTarget_IsNotFound(string mode)
    {
        await using var server = new MiniFtpServer(Options(mode));
        server.Put("/to.zip", [1]);
        await using var files = await ConnectAsync(server);
        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => files.RenameAsync("/from.zip", "/to.zip", None));
        Assert.Equal(RemoteFailure.NotFound, ex.Kind);
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task CreateDirectory_OverFile_IsExists(string mode)
    {
        await using var server = new MiniFtpServer(Options(mode));
        server.Put("/file", [1]);
        await using var files = await ConnectAsync(server);
        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => files.CreateDirectoryAsync("/file", None));
        Assert.Equal(RemoteFailure.Exists, ex.Kind);
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task List_OfFile_IsNotFound_FolderWithSameNamedFileIsListed(string mode)
    {
        await using var server = new MiniFtpServer(Options(mode));
        server.Put("/Mods/a.zip", [1]);
        server.Put("/Mods/Mods", [2]); // папка, где лежит файл с её же именем
        await using var files = await ConnectAsync(server);
        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => files.ListAsync("/Mods/a.zip", None));
        Assert.Equal(RemoteFailure.NotFound, ex.Kind);
        Assert.Equal(2, (await files.ListAsync("/Mods", None)).Count);
    }

    [Fact]
    public void Translate_PasswordOnlyInInnerException_DropsChain()
    {
        const string password = "глубоко-спрятанный-42";
        var endpoint = new HostedEndpoint(HostedProtocol.Ftp, "127.0.0.1", 21, FtpTls.None, "u", password, false, null);
        var files = new FluentFtpFiles(endpoint);
        var ex = new IOException("connection reset", new InvalidOperationException("wrapped", new Exception("server said " + password)));

        var error = files.Translate(ex, "list", "/Mods");
        Assert.Equal(RemoteFailure.Connect, error.Kind);
        Assert.DoesNotContain(password, error.ToString());
    }

    /// <summary>Поток только вперёд, как сетевой: перемотать нельзя.</summary>
    private sealed class ForwardOnlyStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    // ---------- регистрация

    [Fact]
    public void Register_MakesFtpAvailableThroughFactory()
    {
        HostedTransports.Register();
        Assert.True(RemoteFilesFactory.Supports(HostedProtocol.Ftp));
        var endpoint = new HostedEndpoint(HostedProtocol.Ftp, "ftp-" + Guid.NewGuid().ToString("N") + ".test", 21, FtpTls.Auto, "u", "p", false, null);
        Assert.IsType<FluentFtpFiles>(RemoteFilesFactory.Create(endpoint));
    }

    [Fact]
    public void FluentFtp_StaysOutOfAgentAndCore()
    {
        // агент публикуется обрезанным одним exe — FluentFTP ему не нужен; Core про FluentFTP не знает
        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        var agent = File.ReadAllText(Path.Combine(src, "eViSTool.Agent", "eViSTool.Agent.csproj"));
        var core = File.ReadAllText(Path.Combine(src, "eViSTool.Core", "eViSTool.Core.csproj"));
        Assert.DoesNotContain("eViSTool.Hosting", agent);
        Assert.DoesNotContain("FluentFTP", agent);
        Assert.DoesNotContain("FluentFTP", core);
        Assert.DoesNotContain(typeof(FluentFtpFiles).Assembly.GetName().Name,
            typeof(RemoteFilesFactory).Assembly.GetReferencedAssemblies().Select(a => a.Name));
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}

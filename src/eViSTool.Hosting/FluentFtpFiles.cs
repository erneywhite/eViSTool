using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using eViSTool.Core.Hosting;
using FluentFTP;

namespace eViSTool.Hosting;

/// <summary>
/// Файлы сервера у хостера по FTP/FTPS поверх FluentFTP. Один экземпляр — одно управляющее соединение, не потокобезопасен.
/// Наружу выходит только <see cref="RemoteFilesException"/> (и отмена как есть).
/// <para>Устроен под G-Portal, где FEAT, OPTS, STAT, NLST, NOOP и MDTM не реализованы (502): возможности не выясняются
/// по FEAT, а пробуются и запоминаются; живость проверяется командой PWD; NOOP не шлётся никогда; листинг — MLSD с откатом
/// на LIST; время файла — из MLSD, MDTM не нужен. Прерванная посреди файла передача у G-Portal убивает управляющее
/// соединение, поэтому после неё соединение сразу бросается и открывается заново при следующей операции.</para>
/// </summary>
public sealed class FluentFtpFiles(HostedEndpoint endpoint) : IRemoteFiles
{
    /// <summary>Сколько соединение может простоять, прежде чем перед операцией его проверят командой PWD.</summary>
    internal TimeSpan IdleCheck { get; init; } = TimeSpan.FromSeconds(30);

    private AsyncFtpClient? _client;
    private bool _broken;
    private bool? _mlsd;              // null — ещё не пробовали; сервер может уметь MLSD, не объявляя его в FEAT
    private bool _rangeReads = true;  // REST пробуем, пока сервер не откажет
    private bool _passiveConnectionIp; // PASV назвал недоступный адрес — данные идут на адрес управляющего соединения
    private string? _banner;
    private DateTime _lastUse;
    private (string Sha256, string Subject)? _rejectedCertificate;

    public RemoteFeatures Features => new(_rangeReads, Utf8: true, Encrypted: _client?.IsEncrypted ?? false, _banner);

    public bool IsConnected => !_broken && _client is { IsConnected: true };

    public async Task ConnectAsync(CancellationToken ct)
    {
        await DropAsync().ConfigureAwait(false);
        _rejectedCertificate = null;
        var client = new AsyncFtpClient(endpoint.Host, endpoint.User, endpoint.Password, endpoint.Port, BuildConfig())
        {
            // принудительно UTF-8: G-Portal шлёт кириллицу в UTF-8, но не объявляет этого и не знает OPTS
            Encoding = new UTF8Encoding(false),
        };
        client.ValidateCertificate += OnValidateCertificate;
        try
        {
            await client.Connect(ct).ConfigureAwait(false);
            _banner = client.LastReplies?.LastOrDefault(r => r.Code == "220") is { } hello ? "220 " + hello.Message : null;
            await ProbeFeaturesAsync(client, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw Translate(ex, "connect", null);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _client = client;
        _broken = false;
        _lastUse = DateTime.UtcNow;
    }

    private FtpConfig BuildConfig()
    {
        var ms = (int)Math.Clamp(endpoint.Timeout.TotalMilliseconds, 1000, int.MaxValue);
        return new FtpConfig
        {
            EncryptionMode = endpoint.Tls switch
            {
                FtpTls.None => FtpEncryptionMode.None,
                FtpTls.Explicit => FtpEncryptionMode.Explicit,
                FtpTls.Implicit => FtpEncryptionMode.Implicit,
                _ => FtpEncryptionMode.Auto,
            },
            DataConnectionEncryption = true,
            ValidateAnyCertificate = false,
            DataConnectionType = endpoint.ActiveMode ? FtpDataConnectionType.AutoActive
                : _passiveConnectionIp ? FtpDataConnectionType.PASVEX : FtpDataConnectionType.AutoPassive,
            // FEAT шлём сами и 5xx на него считаем «возможностей нет» (G-Portal: 502 'feat' not implemented)
            CheckCapabilities = false,
            // NOOP у G-Portal не реализован: ни фонового NOOP, ни NOOP перед командами
            Noop = false,
            NoopTestConnectivity = false,
            // ответа на QUIT после обрыва можно не дождаться, а ждать незачем
            DisconnectWithQuit = false,
            ConnectTimeout = ms,
            ReadTimeout = ms,
            DataConnectionConnectTimeout = ms,
            DataConnectionReadTimeout = ms,
            // имя и пароль в журнал FluentFTP не попадают; сам журнал никуда не пишется (логгер не задан)
            LogPassword = false,
            LogUserName = false,
            LogToConsole = false,
        };
    }

    /// <summary>FEAT — если сервер его знает. Отказ — не ошибка: MLSD и REST тогда выясняются пробой.</summary>
    private async Task ProbeFeaturesAsync(AsyncFtpClient client, CancellationToken ct)
    {
        var reply = await client.Execute("FEAT", ct).ConfigureAwait(false);
        if (!reply.Success) return;
        var lines = (reply.InfoMessages ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        bool Has(string feature) => lines.Any(l => l.StartsWith(feature, StringComparison.OrdinalIgnoreCase));
        if (Has("MLST")) _mlsd ??= true;
        if (Has("UTF8"))
        {
            // кодировка уже UTF-8; OPTS — чтобы сервер тоже перешёл, отказ не важен
            await client.Execute("OPTS UTF8 ON", ct).ConfigureAwait(false);
        }
    }

    /// <summary>Своя проверка сертификата: верная цепочка, иначе отпечаток совпал с доверенным, иначе отказ — с
    /// отпечатком в исключении, чтобы окно предложило доверять.</summary>
    private void OnValidateCertificate(FluentFTP.Client.BaseClient.BaseFtpClient control, FtpSslValidationEventArgs e)
    {
        if (e.PolicyErrors == SslPolicyErrors.None) { e.Accept = true; return; }
        if (e.Certificate is null) { e.Accept = false; return; }
        var sha = Convert.ToHexString(SHA256.HashData(e.Certificate.GetRawCertData()));
        if (endpoint.TrustedCertificateSha256 is { } trusted && Normalize(trusted) == sha) { e.Accept = true; return; }
        _rejectedCertificate = (sha, e.Certificate.Subject);
        e.Accept = false;
    }

    private static string Normalize(string sha) => sha.Replace(":", "").Replace(" ", "").Trim().ToUpperInvariant();

    /// <summary>Живое соединение перед операцией: нет или порвано — подключиться; долго простаивало — проверить PWD.</summary>
    private async Task<AsyncFtpClient> ReadyAsync(CancellationToken ct)
    {
        if (_client is { } client && !_broken && client.IsConnected && DateTime.UtcNow - _lastUse >= IdleCheck)
        {
            try
            {
                if (!(await client.Execute("PWD", ct).ConfigureAwait(false)).Success) _broken = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { _broken = true; }
        }
        if (_client is null || _broken || !_client.IsConnected) await ConnectAsync(ct).ConfigureAwait(false);
        _lastUse = DateTime.UtcNow;
        return _client!;
    }

    // ---------- чтение

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string dir, CancellationToken ct)
    {
        dir = CheckPath(dir);
        try
        {
            var list = await ListCoreAsync(dir, ct).ConfigureAwait(false);
            // vsftpd на LIST несуществующей папки отвечает пустым списком, а не 550
            if (list.Count == 0 && dir != "/" && await StatCoreAsync(dir, ct).ConfigureAwait(false) is not { IsDirectory: true })
                throw new RemoteFilesException(RemoteFailure.NotFound, $"list {dir}: no such directory");
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw await RefineAsync(Translate(ex, "list", dir), dir, ct).ConfigureAwait(false);
        }
    }

    private Task<IReadOnlyList<RemoteEntry>> ListCoreAsync(string dir, CancellationToken ct) => WithDataRetryAsync(async () =>
    {
        var client = await ReadyAsync(ct).ConfigureAwait(false);
        if (_mlsd != false)
        {
            // FluentFTP шлёт MLSD, только если знает про MLST и разбор машинный; G-Portal MLST не объявляет
            if (!client.HasFeature(FtpCapability.MLST)) client.Capabilities.Add(FtpCapability.MLST);
            client.Config.ListingParser = FtpParser.Machine;
            try
            {
                var items = await client.GetListing(dir, FtpListOption.Auto, ct).ConfigureAwait(false);
                _mlsd = true;
                return ToEntries(dir, items, utc: true);
            }
            catch (FluentFTP.Exceptions.FtpCommandException ex) when (_mlsd is null && IsUnknownCommand(ex.CompletionCode))
            {
                // vsftpd: «500 Unknown command» — MLSD нет, дальше только LIST
                _mlsd = false;
                client.Capabilities.Remove(FtpCapability.MLST);
            }
        }
        client.Capabilities.Remove(FtpCapability.MLST);
        client.Config.ListingParser = FtpParser.Auto;
        // shortcut: время из LIST — местное время сервера с точностью до минуты, пояс неизвестен; кэш по нему
        // перечитает архивы при переводе часов — как в проекте, п. 16 рисков. Переделать, если появится MDTM-проба.
        return ToEntries(dir, await client.GetListing(dir, FtpListOption.ForceList, ct).ConfigureAwait(false), utc: false);
    }, ct);

    private static bool IsUnknownCommand(string? code) => code is "500" or "502" or "504";

    private static IReadOnlyList<RemoteEntry> ToEntries(string dir, FtpListItem[] items, bool utc)
    {
        var list = new List<RemoteEntry>(items.Length);
        foreach (var item in items)
        {
            if (item.Name is "." or ".." || string.IsNullOrEmpty(item.Name)) continue;
            if (item.Type is not (FtpObjectType.File or FtpObjectType.Directory)) continue;
            DateTime? modified = item.Modified == DateTime.MinValue ? null
                : DateTime.SpecifyKind(item.Modified, utc ? DateTimeKind.Utc : DateTimeKind.Unspecified);
            var isDir = item.Type == FtpObjectType.Directory;
            list.Add(new RemoteEntry(RemotePath.Combine(dir, item.Name), item.Name, isDir, isDir ? 0 : Math.Max(0, item.Size), modified));
        }
        return list;
    }

    /// <summary>Сведения о пути — строкой листинга родительской папки: MLST/MDTM у хостеров ненадёжны, а так время и
    /// размер совпадают с тем, что вернул <see cref="ListAsync"/> (кэш modinfo сверяет именно их).</summary>
    public async Task<RemoteEntry?> StatAsync(string path, CancellationToken ct)
    {
        path = CheckPath(path);
        if (path == "/") return new RemoteEntry("/", "", true, 0, null);
        try { return await StatCoreAsync(path, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw Translate(ex, "stat", path);
        }
    }

    private async Task<RemoteEntry?> StatCoreAsync(string path, CancellationToken ct)
    {
        IReadOnlyList<RemoteEntry> siblings;
        try { siblings = await ListCoreAsync(RemotePath.Parent(path), ct).ConfigureAwait(false); }
        catch (FluentFTP.Exceptions.FtpCommandException ex) when (ex.CompletionCode == "550") { return null; } // нет родителя
        var name = RemotePath.Name(path);
        return siblings.FirstOrDefault(e => e.Name == name);
    }

    public async Task<byte[]> ReadRangeAsync(string path, long offset, int count, CancellationToken ct)
    {
        path = CheckPath(path);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > 0 && !_rangeReads) throw new RemoteFilesException(RemoteFailure.RangeUnsupported, $"read {path}: server cannot resume (REST)");
        try { return await ReadRangeCoreAsync(path, offset, count, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (ex is RemoteFilesException { Kind: RemoteFailure.RangeUnsupported }) throw;
            throw await RefineAsync(Translate(ex, "read", path), path, ct).ConfigureAwait(false);
        }
    }

    private Task<byte[]> ReadRangeCoreAsync(string path, long offset, int count, CancellationToken ct) => WithDataRetryAsync(async () =>
    {
        var client = await ReadyAsync(ct).ConfigureAwait(false);
        Stream stream;
        try { stream = await client.OpenRead(path, FtpDataType.Binary, offset, checkIfFileExists: false, ct).ConfigureAwait(false); }
        catch (FluentFTP.Exceptions.FtpCommandException) when (offset > 0)
        {
            if (!await RestRefusedAsync(client, ct).ConfigureAwait(false)) throw;
            _rangeReads = false;
            throw new RemoteFilesException(RemoteFailure.RangeUnsupported, $"read {path}: server cannot resume (REST)");
        }
        var buffer = new byte[count];
        var read = 0;
        var atEnd = false;
        try
        {
            while (read < count)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
                if (n == 0) { atEnd = true; break; }
                read += n;
            }
            // ровно count байт — не конец ли это файла: один лишний байт отличит конец от середины
            if (!atEnd) atEnd = await stream.ReadAsync(new byte[1], ct).ConfigureAwait(false) == 0;
        }
        catch
        {
            await AbandonAsync(stream).ConfigureAwait(false);
            throw;
        }
        if (atEnd)
        {
            // передача кончилась сама: сервер ответит 226, соединение остаётся рабочим
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            // посреди файла: G-Portal после такого обрыва больше не отвечает по управляющему соединению, vsftpd шлёт 426.
            // Ждать ответа нечего — соединение бросаем, следующая операция откроет новое (вход ~0,13 с)
            await AbandonAsync(stream).ConfigureAwait(false);
        }
        return read == count ? buffer : buffer[..read];
    }, ct);

    /// <summary>Отказ при чтении с позиции — из-за REST? Проверяем отдельной командой «REST 0».</summary>
    private static async Task<bool> RestRefusedAsync(AsyncFtpClient client, CancellationToken ct)
    {
        try { return (await client.Execute("REST 0", ct).ConfigureAwait(false)).Code is ['5', ..]; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    /// <summary>Бросить соединение вместе с недочитанной передачей, не дожидаясь ответа сервера.</summary>
    private async Task AbandonAsync(Stream stream)
    {
        _broken = true;
        var client = _client;
        _client = null;
        // сначала управляющее соединение: иначе закрытие передачи ждало бы ответа сервера, которого у G-Portal не будет
        if (client is not null)
        {
            try { await client.DisposeAsync().ConfigureAwait(false); } catch (Exception) { /* уже порвано */ }
        }
        try { await stream.DisposeAsync().ConfigureAwait(false); } catch (Exception) { /* уже порвано */ }
    }

    public async Task DownloadAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        path = CheckPath(path);
        var start = destination.CanSeek ? destination.Position : 0;
        try
        {
            await WithDataRetryAsync(async () =>
            {
                var client = await ReadyAsync(ct).ConfigureAwait(false);
                if (destination.CanSeek) { destination.Position = start; destination.SetLength(start); }
                if (!await client.DownloadStream(destination, path, 0, Bytes(progress), ct).ConfigureAwait(false))
                    throw new RemoteFilesException(RemoteFailure.Protocol, $"download {path}: transfer failed");
                return true;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw await RefineAsync(Translate(ex, "download", path), path, ct).ConfigureAwait(false);
        }
    }

    // ---------- запись

    public async Task UploadAsync(Stream source, string path, IProgress<long>? progress, CancellationToken ct)
    {
        path = CheckPath(path);
        var start = source.CanSeek ? source.Position : 0;
        try
        {
            await WithDataRetryAsync(async () =>
            {
                var client = await ReadyAsync(ct).ConfigureAwait(false);
                if (source.CanSeek) source.Position = start;
                // NoCheck: сразу STOR, без проверки существования (она стоила бы лишнего листинга)
                var status = await client.UploadStream(source, path, FtpRemoteExists.NoCheck, false, Bytes(progress), ct).ConfigureAwait(false);
                if (status != FtpStatus.Success) throw new RemoteFilesException(RemoteFailure.Protocol, $"upload {path}: transfer failed");
                return true;
            }, ct, canRetry: source.CanSeek).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw Translate(ex, "upload", path);
        }
    }

    public async Task RenameAsync(string from, string to, CancellationToken ct)
    {
        from = CheckPath(from);
        to = CheckPath(to);
        try
        {
            var client = await ReadyAsync(ct).ConfigureAwait(false);
            // простые RNFR/RNTO: MoveFile с перезаписью заранее удаляет назначение, а это решает вызывающий
            var rnfr = await client.Execute("RNFR " + from, ct).ConfigureAwait(false);
            if (rnfr.Code != "350") throw Failed(rnfr, "rename", from);
            var rnto = await client.Execute("RNTO " + to, ct).ConfigureAwait(false);
            if (!rnto.Success) throw Failed(rnto, "rename", to);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var error = Translate(ex, "rename", from);
            if (error.Kind is not (RemoteFailure.NotFound or RemoteFailure.Denied or RemoteFailure.Protocol)) throw error;
            // G-Portal поверх не переименовывает: «550 path already exists»
            if (await ExistsQuietAsync(to, ct).ConfigureAwait(false) == true)
                throw new RemoteFilesException(RemoteFailure.Exists, $"rename {from} -> {to}: target exists", ex);
            if (await ExistsQuietAsync(from, ct).ConfigureAwait(false) == false)
                throw new RemoteFilesException(RemoteFailure.NotFound, $"rename {from}: not found", ex);
            throw error;
        }
    }

    public async Task DeleteFileAsync(string path, CancellationToken ct)
    {
        path = CheckPath(path);
        try
        {
            var client = await ReadyAsync(ct).ConfigureAwait(false);
            var reply = await client.Execute("DELE " + path, ct).ConfigureAwait(false);
            if (!reply.Success) throw Failed(reply, "delete", path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw await RefineAsync(Translate(ex, "delete", path), path, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Создать папку вместе с недостающими родителями; уже есть — не ошибка.</summary>
    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        path = CheckPath(path);
        if (path == "/") return;
        try
        {
            var client = await ReadyAsync(ct).ConfigureAwait(false);
            var reply = await client.Execute("MKD " + path, ct).ConfigureAwait(false);
            if (reply.Success) return;
            var existing = await StatCoreAsync(path, ct).ConfigureAwait(false);
            if (existing is { IsDirectory: true }) return;
            if (existing is null && await StatCoreAsync(RemotePath.Parent(path), ct).ConfigureAwait(false) is null)
            {
                await CreateDirectoryAsync(RemotePath.Parent(path), ct).ConfigureAwait(false);
                client = await ReadyAsync(ct).ConfigureAwait(false);
                reply = await client.Execute("MKD " + path, ct).ConfigureAwait(false);
                if (reply.Success) return;
            }
            throw Failed(reply, "mkdir", path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw Translate(ex, "mkdir", path);
        }
    }

    public async ValueTask DisposeAsync() => await DropAsync().ConfigureAwait(false);

    private async Task DropAsync()
    {
        var client = _client;
        _client = null;
        if (client is null) return;
        try { await client.DisposeAsync().ConfigureAwait(false); } catch (Exception) { /* закрываем то, что уже порвано */ }
    }

    // ---------- общее

    /// <summary>Передача не открылась: если PASV назвал адрес, до которого не достучаться (NAT хостера), — один повтор
    /// с адресом управляющего соединения.</summary>
    private async Task<T> WithDataRetryAsync<T>(Func<Task<T>> work, CancellationToken ct, bool canRetry = true)
    {
        try { return await work().ConfigureAwait(false); }
        catch (Exception ex) when (canRetry && !endpoint.ActiveMode && !_passiveConnectionIp && !ct.IsCancellationRequested && IsDataConnectFailure(ex))
        {
            // shortcut: PASVEX работает только по IPv4; если появятся хостеры с FTP по IPv6 — повторять через EPSV
            _passiveConnectionIp = true;
            if (_client is { } client) client.Config.DataConnectionType = FtpDataConnectionType.PASVEX;
            return await work().ConfigureAwait(false);
        }
    }

    private static bool IsDataConnectFailure(Exception ex) =>
        TranslateStatic(ex, "").Kind is RemoteFailure.Connect or RemoteFailure.Timeout && ex is not RemoteFilesException;

    private static IProgress<FtpProgress>? Bytes(IProgress<long>? progress) =>
        progress is null ? null : new BytesProgress(progress);

    /// <summary>Прогресс FluentFTP → байты, сразу и в том же потоке: в какой поток передать, решает сам получатель.</summary>
    private sealed class BytesProgress(IProgress<long> target) : IProgress<FtpProgress>
    {
        public void Report(FtpProgress value) => target.Report(value.TransferredBytes);
    }

    /// <summary>Путь из Core уже нормализован; здесь — страховка от перевода строки в команде FTP.</summary>
    private static string CheckPath(string path)
    {
        if (path.AsSpan().IndexOfAny('\r', '\n') >= 0) throw new RemoteFilesException(RemoteFailure.Denied, "bad path");
        return RemotePath.Normalize(path);
    }

    private async Task<bool?> ExistsQuietAsync(string path, CancellationToken ct)
    {
        try { return await StatCoreAsync(path, ct).ConfigureAwait(false) is not null; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>550 бывает и «нет такого», и «нельзя»: уточняем по листингу.</summary>
    private async Task<RemoteFilesException> RefineAsync(RemoteFilesException error, string path, CancellationToken ct)
    {
        if (error.Kind is not (RemoteFailure.NotFound or RemoteFailure.Denied)) return error;
        return await ExistsQuietAsync(path, ct).ConfigureAwait(false) switch
        {
            false when error.Kind != RemoteFailure.NotFound => new RemoteFilesException(RemoteFailure.NotFound, error.Message, error.InnerException),
            true when error.Kind != RemoteFailure.Denied => new RemoteFilesException(RemoteFailure.Denied, error.Message, error.InnerException),
            _ => error,
        };
    }

    private RemoteFilesException Failed(FtpReply reply, string op, string path) =>
        FromCode(reply.Code, $"{op} {path}: {reply.Code} {reply.Message}", null);

    private static RemoteFilesException FromCode(string? code, string message, Exception? inner) => new(code switch
    {
        "530" or "331" or "332" => RemoteFailure.Auth,
        "550" or "450" => RemoteFailure.NotFound,
        "552" or "452" => RemoteFailure.Quota,
        "553" or "532" => RemoteFailure.Denied,
        "421" or "425" or "426" => RemoteFailure.Connect,
        _ => RemoteFailure.Protocol,
    }, message, inner);

    /// <summary>Любое исключение FluentFTP и сети — в <see cref="RemoteFilesException"/>. Пароль в текст не попадает:
    /// сообщения строятся из ответа сервера, пути и текста ошибки сокета, а на всякий случай ещё и вычищаются.</summary>
    private RemoteFilesException Translate(Exception ex, string op, string? path)
    {
        var where = path is null ? op : $"{op} {path}";
        if (ex is RemoteFilesException rfe) return rfe;
        if (_rejectedCertificate is { } cert && Chain(ex).Any(e => e is AuthenticationException or FluentFTP.Exceptions.FtpInvalidCertificateException))
        {
            return new RemoteFilesException(RemoteFailure.CertificateUntrusted, Scrub($"{where}: untrusted certificate {cert.Subject}"), ex)
            {
                CertificateSha256 = cert.Sha256,
                CertificateSubject = cert.Subject,
            };
        }
        var translated = TranslateStatic(ex, where);
        return translated.Message.Contains(endpoint.Password, StringComparison.Ordinal) && endpoint.Password.Length > 0
            ? new RemoteFilesException(translated.Kind, Scrub(translated.Message), null)
            : translated;
    }

    private static RemoteFilesException TranslateStatic(Exception ex, string where)
    {
        foreach (var e in Chain(ex))
        {
            switch (e)
            {
                case RemoteFilesException rfe: return rfe;
                case FluentFTP.Exceptions.FtpAuthenticationException:
                    return new RemoteFilesException(RemoteFailure.Auth, $"{where}: {e.Message}", ex);
                case FluentFTP.Exceptions.FtpCommandException cmd:
                    return FromCode(cmd.CompletionCode, $"{where}: {cmd.CompletionCode} {cmd.Message}", ex);
                case FluentFTP.Exceptions.FtpSecurityNotAvailableException or FluentFTP.Exceptions.FtpInvalidCertificateException or AuthenticationException:
                    return new RemoteFilesException(RemoteFailure.Tls, $"{where}: {e.Message}", ex);
                case TimeoutException or OperationCanceledException:
                    return new RemoteFilesException(RemoteFailure.Timeout, $"{where}: timed out", ex);
                case System.Net.Sockets.SocketException or FluentFTP.Exceptions.FtpMissingSocketException:
                    return new RemoteFilesException(RemoteFailure.Connect, $"{where}: {e.Message}", ex);
            }
        }
        // обрыв посреди ответа FluentFTP отдаёт голым IOException, без сокета внутри
        if (Chain(ex).Any(e => e is IOException)) return new RemoteFilesException(RemoteFailure.Connect, $"{where}: {ex.Message}", ex);
        return new RemoteFilesException(RemoteFailure.Protocol, $"{where}: {ex.Message}", ex);
    }

    private static IEnumerable<Exception> Chain(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            yield return e;
            if (e is AggregateException agg)
                foreach (var inner in agg.InnerExceptions)
                    foreach (var x in Chain(inner)) yield return x;
        }
    }

    private string Scrub(string text) => endpoint.Password.Length > 0 ? text.Replace(endpoint.Password, "***", StringComparison.Ordinal) : text;
}

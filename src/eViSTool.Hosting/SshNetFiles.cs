using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using eViSTool.Core.Hosting;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using Renci.SshNet.Sftp;

namespace eViSTool.Hosting;

/// <summary>
/// Файлы сервера у хостера по SFTP поверх SSH.NET. Один экземпляр — одно соединение SSH, не потокобезопасен.
/// Наружу выходит только <see cref="RemoteFilesException"/> (и отмена как есть).
/// <para>Вход — по паролю (и keyboard-interactive с тем же паролем: так часто устроен вход по паролю на Linux) или по
/// файлу закрытого ключа на этом компьютере. Ключ сервера сверяется с отпечатком из профиля: незнакомый — отказ с
/// отпечатком, чтобы окно предложило довериться; не тот, которому доверяли, — отказ «ключ сменился». Чтение с позиции —
/// своё у SFTP (смещение в каждом запросе), время файлов — UTC. Переименование — обычное SSH_FXP_RENAME: поверх
/// существующего файла серверы его обычно не делают, и тогда адаптер отвечает <see cref="RemoteFailure.Exists"/>.</para>
/// </summary>
public sealed partial class SshNetFiles(HostedEndpoint endpoint) : IRemoteFiles
{
    private SftpClient? _client;
    // ключ и способы входа живут, пока живо соединение: закрылось — их секреты уходят вместе с ним
    private PrivateKeyFile? _key;
    private List<AuthenticationMethod>? _methods;
    private bool _broken;
    private string? _banner;
    private (string Sha256, string Name)? _rejectedHostKey;

    public RemoteFeatures Features => new(RangeReads: true, Utf8: true, Encrypted: true, _banner);

    public bool IsConnected => !_broken && _client is { IsConnected: true };

    public async Task ConnectAsync(CancellationToken ct)
    {
        await DropAsync().ConfigureAwait(false);
        _rejectedHostKey = null;
        if (string.IsNullOrWhiteSpace(endpoint.User)) throw new RemoteFilesException(RemoteFailure.Auth, "connect: the login is empty");
        SftpClient? client = null;
        try
        {
            // ключ читается и расшифровывается (у OpenSSH — bcrypt, это заметное время), а вход и обмен ключами у SSH.NET
            // местами ждут синхронно — всё это не в потоке окна
            client = await Task.Run(() => Create(), ct).ConfigureAwait(false);
            await Task.Run(() => client.ConnectAsync(ct), ct).ConfigureAwait(false);
            _banner = client.ConnectionInfo.ServerVersion;
            // shortcut: пути — от «/» сервера SFTP, как у хостеров с chroot (корень = папка сервера). На своём сервере без
            // chroot папку сервера человек пишет сам в «Папка сервера»; появятся такие хостеры — подсказывать домашнюю
            // папку (client.WorkingDirectory после входа)
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
            DropSecrets();
            throw Translate(ex, "connect", null);
        }
        catch
        {
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
            DropSecrets();
            throw;
        }
        _client = client;
        _broken = false;
    }

    private SftpClient Create()
    {
        var methods = new List<AuthenticationMethod>();
        if (endpoint.KeyFile is { } keyFile)
        {
            _key = LoadKey(keyFile);
            methods.Add(new PrivateKeyAuthenticationMethod(endpoint.User, _key));
        }
        // по ключу пароль не нужен; задан и он — пробуется следом (сервер может требовать и то и другое)
        if (endpoint.KeyFile is null || endpoint.Password.Length > 0)
        {
            methods.Add(new PasswordAuthenticationMethod(endpoint.User, endpoint.Password));
            var interactive = new KeyboardInteractiveAuthenticationMethod(endpoint.User);
            // скрытый ввод — пароль, видимый — логин; на чужие вопросы (код из приложения) ответ просто не подойдёт
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = prompt.IsEchoed ? endpoint.User : endpoint.Password;
            };
            methods.Add(interactive);
        }
        var info = new ConnectionInfo(endpoint.Host, endpoint.Port, endpoint.User, [.. methods])
        {
            Timeout = Clamp(endpoint.Timeout),
            Encoding = new UTF8Encoding(false),
        };
        _methods = methods;
        var client = new SftpClient(info) { OperationTimeout = Clamp(endpoint.Timeout) };
        client.HostKeyReceived += OnHostKey;
        return client;
    }

    private static TimeSpan Clamp(TimeSpan timeout) => TimeSpan.FromMilliseconds(Math.Clamp(timeout.TotalMilliseconds, 1000, int.MaxValue));

    /// <summary>Файл ключа: прочитать и открыть. Беда с ним — <see cref="RemoteFailure.KeyFile"/> или
    /// <see cref="RemoteFailure.KeyPassphrase"/>: до сервера дело ещё не дошло.</summary>
    private PrivateKeyFile LoadKey(string keyFile)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(HostedEndpoint.ExpandKeyPath(keyFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new RemoteFilesException(RemoteFailure.KeyFile, Scrub($"{keyFile}: {ex.Message}"), ex);
        }
        var passphrase = string.IsNullOrEmpty(endpoint.KeyPassphrase) ? null : endpoint.KeyPassphrase;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return new PrivateKeyFile(stream, passphrase);
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            throw new RemoteFilesException(RemoteFailure.KeyPassphrase, $"{keyFile}: the key is protected by a passphrase");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // зашифрованный ключ не открылся: без пароля ключа — он и нужен (не все форматы SSH.NET говорят об этом
            // своим исключением), с паролем — почти всегда пароль не тот. Внутреннее исключение не прикладываем: разбор
            // ключа мог положить в текст что угодно из файла
            if (KeyLooksEncrypted(bytes))
                throw new RemoteFilesException(RemoteFailure.KeyPassphrase,
                    passphrase is null ? $"{keyFile}: the key is protected by a passphrase" : $"{keyFile}: wrong passphrase");
            throw new RemoteFilesException(RemoteFailure.KeyFile, Scrub($"{keyFile}: {ex.Message}"));
        }
    }

    /// <summary>
    /// Закрытый ключ зашифрован паролем: PEM с «Proc-Type: 4,ENCRYPTED», PKCS#8 «ENCRYPTED PRIVATE KEY», OpenSSH с шифром
    /// не «none», PuTTY с «Encryption:» не «none». Не разобрать — false (тогда это просто «не тот файл»).
    /// </summary>
    internal static bool KeyLooksEncrypted(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.ASCII.GetString(bytes);
        if (text.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.OrdinalIgnoreCase)
            || text.Contains("BEGIN ENCRYPTED PRIVATE KEY", StringComparison.Ordinal)) return true;
        if (PuttyEncryption().Match(text) is { Success: true } putty) return putty.Groups[1].Value != "none";
        if (OpenSshBlock().Match(text) is not { Success: true } block) return false;
        try
        {
            var blob = Convert.FromBase64String(Regex.Replace(block.Groups[1].Value, @"\s+", ""));
            var magic = "openssh-key-v1\0"u8;
            if (blob.Length < magic.Length + 4 || !blob.AsSpan(0, magic.Length).SequenceEqual(magic)) return false;
            var length = BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(magic.Length, 4));
            if (length <= 0 || magic.Length + 4 + length > blob.Length) return false;
            return Encoding.ASCII.GetString(blob, magic.Length + 4, length) != "none";
        }
        catch (FormatException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^Encryption:\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex PuttyEncryption();

    [GeneratedRegex(@"-----BEGIN OPENSSH PRIVATE KEY-----(.*?)-----END OPENSSH PRIVATE KEY-----", RegexOptions.Singleline)]
    private static partial Regex OpenSshBlock();

    /// <summary>Ключ сервера: тот, которому доверяли, — дальше; иначе отказ, а отпечаток — в исключение (<see cref="Translate"/>).</summary>
    private void OnHostKey(object? sender, HostKeyEventArgs e)
    {
        var sha = "SHA256:" + e.FingerPrintSHA256;
        if (endpoint.TrustedHostKey is { } trusted && SameFingerprint(trusted, sha))
        {
            e.CanTrust = true;
            return;
        }
        _rejectedHostKey = (sha, $"{e.HostKeyName} {e.KeyLength}");
        e.CanTrust = false;
    }

    /// <summary>Отпечатки совпадают: с «SHA256:» и без, с «=» в конце и без (base64 — с учётом регистра).</summary>
    internal static bool SameFingerprint(string a, string b) => string.Equals(Fingerprint(a), Fingerprint(b), StringComparison.Ordinal);

    private static string Fingerprint(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)) t = t[7..];
        return t.TrimEnd('=');
    }

    /// <summary>Живое соединение перед операцией: нет или порвано — подключиться заново.</summary>
    private async Task<SftpClient> ReadyAsync(CancellationToken ct)
    {
        if (_client is null || _broken || !_client.IsConnected) await ConnectAsync(ct).ConfigureAwait(false);
        return _client!;
    }

    // ---------- чтение

    /// <summary>Перечислить папку. Папки нет или по пути лежит файл — <see cref="RemoteFailure.NotFound"/>.</summary>
    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string dir, CancellationToken ct)
    {
        dir = CheckPath(dir);
        try
        {
            var client = await ReadyAsync(ct).ConfigureAwait(false);
            var list = new List<RemoteEntry>();
            await foreach (var file in client.ListDirectoryAsync(dir, ct).ConfigureAwait(false))
            {
                if (file.Name is "." or ".." || string.IsNullOrEmpty(file.Name) || file.Name.AsSpan().IndexOfAny('/', '\\') >= 0) continue;
                string path;
                try { path = RemotePath.Combine(dir, file.Name); }
                catch (InvalidDataException) { continue; } // управляющие знаки в имени — такой файл не наш
                var attributes = file.Attributes;
                if (file.IsSymbolicLink)
                {
                    // листинг описывает саму ссылку; папка ли за ней и какого размера файл — по ней самой
                    try { attributes = await client.GetAttributesAsync(path, ct).ConfigureAwait(false); }
                    catch (SftpException) { continue; } // битая ссылка
                }
                if (!attributes.IsDirectory && !attributes.IsRegularFile) continue;
                list.Add(Entry(path, file.Name, attributes));
            }
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var error = Translate(ex, "list", dir);
            // общий отказ на файле вместо папки — по договору «нет такой папки»
            if (error.Kind is RemoteFailure.Protocol or RemoteFailure.Denied
                && await StatQuietAsync(dir, ct).ConfigureAwait(false) is { IsDirectory: false })
                throw new RemoteFilesException(RemoteFailure.NotFound, $"list {dir}: not a directory", error.InnerException);
            throw error;
        }
    }

    private static RemoteEntry Entry(string path, string name, SftpFileAttributes a)
    {
        var modified = a.LastWriteTimeUtc;
        DateTime? utc = modified > DateTime.UnixEpoch ? DateTime.SpecifyKind(modified, DateTimeKind.Utc) : null;
        var isDir = a.IsDirectory;
        return new RemoteEntry(path, name, isDir, isDir ? 0 : Math.Max(0, a.Size), utc);
    }

    /// <summary>Сведения о пути (STAT: ссылки разворачиваются, как и в <see cref="ListAsync"/>); нет — null.</summary>
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
        var client = await ReadyAsync(ct).ConfigureAwait(false);
        try
        {
            return Entry(path, RemotePath.Name(path), await client.GetAttributesAsync(path, ct).ConfigureAwait(false));
        }
        catch (SftpException ex) when (ex is SftpPathNotFoundException || IsNoSuchFile(ex))
        {
            return null;
        }
    }

    /// <summary>Есть ли путь; не удалось узнать — null.</summary>
    private async Task<RemoteEntry?> StatQuietAsync(string path, CancellationToken ct)
    {
        try { return await StatCoreAsync(path, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private async Task<bool?> ExistsQuietAsync(string path, CancellationToken ct)
    {
        try { return await StatCoreAsync(path, ct).ConfigureAwait(false) is not null; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    public async Task<byte[]> ReadRangeAsync(string path, long offset, int count, CancellationToken ct)
    {
        path = CheckPath(path);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        try
        {
            var client = await ReadyAsync(ct).ConfigureAwait(false);
            var stream = await client.OpenAsync(path, FileMode.Open, FileAccess.Read, ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                if (offset > 0) stream.Seek(offset, SeekOrigin.Begin);
                var buffer = new byte[count];
                var read = 0;
                while (read < count)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
                    if (n == 0) break; // конец файла
                    read += n;
                }
                return read == count ? buffer : buffer[..read];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw Translate(ex, "read", path);
        }
    }

    public async Task DownloadAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        path = CheckPath(path);
        try
        {
            var client = await ReadyAsync(ct).ConfigureAwait(false);
            await client.DownloadFileAsync(path, destination, progress is null ? null : new DownloadBytes(progress), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw Translate(ex, "download", path);
        }
    }

    // ---------- запись

    /// <summary>Залить файл; существующий перезаписывается (открытие с усечением).</summary>
    public async Task UploadAsync(Stream source, string path, IProgress<long>? progress, CancellationToken ct)
    {
        path = CheckPath(path);
        try
        {
            var client = await ReadyAsync(ct).ConfigureAwait(false);
            await client.UploadFileAsync(source, path, canOverride: true, progress is null ? null : new UploadBytes(progress), ct)
                .ConfigureAwait(false);
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
            await client.RenameFileAsync(from, to, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var error = Translate(ex, "rename", from);
            if (error.Kind is not (RemoteFailure.NotFound or RemoteFailure.Denied or RemoteFailure.Protocol or RemoteFailure.Exists)) throw error;
            // сначала источник: нет его — переименовывать нечего, чем бы ни было занято назначение
            if (await ExistsQuietAsync(from, ct).ConfigureAwait(false) == false)
                throw new RemoteFilesException(RemoteFailure.NotFound, $"rename {from}: not found", error.InnerException);
            // OpenSSH и большинство серверов на SSH_FXP_RENAME поверх файла отвечают общим «Failure»
            if (await ExistsQuietAsync(to, ct).ConfigureAwait(false) == true)
                throw new RemoteFilesException(RemoteFailure.Exists, $"rename {from} -> {to}: target exists", error.InnerException);
            throw error;
        }
    }

    public async Task DeleteFileAsync(string path, CancellationToken ct)
    {
        path = CheckPath(path);
        try
        {
            var client = await ReadyAsync(ct).ConfigureAwait(false);
            await client.DeleteFileAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw Translate(ex, "delete", path);
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
            try
            {
                await client.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
                return;
            }
            catch (SftpException)
            {
                // «уже есть» у SFTP v3 — общий отказ: смотрим, что там на самом деле
                var existing = await StatCoreAsync(path, ct).ConfigureAwait(false);
                if (existing is { IsDirectory: true }) return;
                if (existing is not null) throw new RemoteFilesException(RemoteFailure.Exists, $"mkdir {path}: a file with this name exists");
                if (await StatCoreAsync(RemotePath.Parent(path), ct).ConfigureAwait(false) is not null) throw;
            }
            await CreateDirectoryAsync(RemotePath.Parent(path), ct).ConfigureAwait(false);
            client = await ReadyAsync(ct).ConfigureAwait(false);
            await client.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
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
        await DisposeQuietlyAsync(client).ConfigureAwait(false);
        DropSecrets();
    }

    private void DropSecrets()
    {
        var key = _key;
        var methods = _methods;
        _key = null;
        _methods = null;
        foreach (var method in methods ?? []) (method as IDisposable)?.Dispose();
        key?.Dispose();
    }

    /// <summary>Закрыть соединение: у SSH.NET это синхронное прощание с сервером, на мёртвой связи — с ожиданием,
    /// поэтому не в потоке окна.</summary>
    private static async Task DisposeQuietlyAsync(SftpClient? client)
    {
        if (client is null) return;
        await Task.Run(() =>
        {
            try { client.Dispose(); }
            catch (Exception) { /* закрываем то, что уже порвано */ }
        }).ConfigureAwait(false);
    }

    // ---------- общее

    private sealed class DownloadBytes(IProgress<long> target) : IProgress<DownloadFileProgressReport>
    {
        public void Report(DownloadFileProgressReport value) => target.Report((long)value.TotalBytesDownloaded);
    }

    private sealed class UploadBytes(IProgress<long> target) : IProgress<UploadFileProgressReport>
    {
        public void Report(UploadFileProgressReport value) => target.Report((long)value.TotalBytesUploaded);
    }

    /// <summary>Путь из Core уже нормализован; здесь — страховка от «..» и управляющих знаков.</summary>
    private static string CheckPath(string path)
    {
        try { return RemotePath.Normalize(path); }
        catch (InvalidDataException ex) { throw new RemoteFilesException(RemoteFailure.Denied, $"bad path: {ex.Message}", ex); }
    }

    private static bool IsNoSuchFile(SftpException ex) => (int)ex.StatusCode is 2 or 10; // NO_SUCH_FILE, NO_SUCH_PATH (v4+)

    /// <summary>Любое исключение SSH.NET и сети — в <see cref="RemoteFilesException"/>. Пароль и пароль ключа в текст не
    /// попадают: сообщения строятся из ответа сервера, пути и текста ошибки, а на всякий случай ещё и вычищаются.</summary>
    internal RemoteFilesException Translate(Exception ex, string op, string? path)
    {
        var where = path is null ? op : $"{op} {path}";
        if (ex is RemoteFilesException rfe) return Mark(rfe);
        // секрет хоть где-то в цепочке (и во внутренних исключениях) — цепочку не прикладываем вовсе
        var leaks = Leaks(ex.ToString());
        if (_rejectedHostKey is { } key && Chain(ex).Any(e => e is SshConnectionException))
        {
            return new RemoteFilesException(RemoteFailure.CertificateUntrusted, $"{where}: untrusted host key {key.Name}", leaks ? null : ex)
            {
                CertificateSha256 = key.Sha256,
                CertificateSubject = key.Name,
                IsHostKey = true,
                HostKeyChanged = endpoint.TrustedHostKey is not null,
            };
        }
        var translated = TranslateStatic(ex, where);
        return Mark(leaks || Leaks(translated.Message) ? new RemoteFilesException(translated.Kind, Scrub(translated.Message), null) : translated);
    }

    /// <summary>Обрыв или молчание — соединение больше не годится: следующая операция откроет новое.</summary>
    private RemoteFilesException Mark(RemoteFilesException error)
    {
        if (error.Kind is RemoteFailure.Connect or RemoteFailure.Timeout) _broken = true;
        return error;
    }

    internal static RemoteFilesException TranslateStatic(Exception ex, string where)
    {
        foreach (var e in Chain(ex))
        {
            switch (e)
            {
                case RemoteFilesException rfe: return rfe;
                case SshPassPhraseNullOrEmptyException:
                    return new RemoteFilesException(RemoteFailure.KeyPassphrase, $"{where}: the key is protected by a passphrase", ex);
                case SshAuthenticationException:
                    return new RemoteFilesException(RemoteFailure.Auth, $"{where}: {e.Message}", ex);
                case SftpPathNotFoundException:
                    return new RemoteFilesException(RemoteFailure.NotFound, $"{where}: {e.Message}", ex);
                case SftpPermissionDeniedException:
                    return new RemoteFilesException(RemoteFailure.Denied, $"{where}: {e.Message}", ex);
                case SftpException sftp:
                    return new RemoteFilesException(FromStatus(sftp), $"{where}: {sftp.StatusCode} {sftp.Message}", ex);
                case SshOperationTimeoutException:
                    return new RemoteFilesException(RemoteFailure.Timeout, $"{where}: timed out", ex);
                case SshConnectionException conn:
                    return new RemoteFilesException(FromDisconnect(conn.DisconnectReason), $"{where}: {conn.Message}", ex);
                case ProxyException:
                    return new RemoteFilesException(RemoteFailure.Connect, $"{where}: {e.Message}", ex);
                case TimeoutException or OperationCanceledException:
                    return new RemoteFilesException(RemoteFailure.Timeout, $"{where}: timed out", ex);
                case SocketException socket:
                    return new RemoteFilesException(socket.SocketErrorCode == SocketError.TimedOut ? RemoteFailure.Timeout : RemoteFailure.Connect,
                        $"{where}: {e.Message}", ex);
                case ObjectDisposedException:
                    // SSH.NET закрыл сеанс сам (связь порвалась), пока шла операция
                    return new RemoteFilesException(RemoteFailure.Connect, $"{where}: the connection was closed", ex);
            }
        }
        if (Chain(ex).Any(e => e is IOException)) return new RemoteFilesException(RemoteFailure.Connect, $"{where}: {ex.Message}", ex);
        return new RemoteFilesException(RemoteFailure.Protocol, $"{where}: {ex.Message}", ex);
    }

    /// <summary>Код ответа SFTP. У версии 3 (OpenSSH) кодов мало: «нет места» и «уже есть» приходят общим «Failure», а
    /// серверы новее шлют и коды версий 4–6 — их тоже понимаем.</summary>
    private static RemoteFailure FromStatus(SftpException ex) => (int)ex.StatusCode switch
    {
        2 or 10 => RemoteFailure.NotFound,     // NO_SUCH_FILE, NO_SUCH_PATH
        3 or 12 => RemoteFailure.Denied,       // PERMISSION_DENIED, WRITE_PROTECT
        11 => RemoteFailure.Exists,            // FILE_ALREADY_EXISTS
        14 or 15 => RemoteFailure.Quota,       // NO_SPACE_ON_FILESYSTEM, QUOTA_EXCEEDED
        6 or 7 => RemoteFailure.Connect,       // NO_CONNECTION, CONNECTION_LOST
        _ when NoSpace(ex.Message) => RemoteFailure.Quota,
        _ => RemoteFailure.Protocol,
    };

    private static bool NoSpace(string? message) =>
        message is not null && (message.Contains("no space", StringComparison.OrdinalIgnoreCase)
                                || message.Contains("quota", StringComparison.OrdinalIgnoreCase)
                                || message.Contains("disk full", StringComparison.OrdinalIgnoreCase));

    /// <summary>Почему сервер закрыл соединение: отказ во входе — <see cref="RemoteFailure.Auth"/>; не договорились о
    /// протоколе (другая версия SSH, нет общих алгоритмов) — <see cref="RemoteFailure.Protocol"/>; остальное — связь.
    /// Не SSH на порту вовсе (FTP) SSH.NET так не распознаёт: строки до «SSH-2.0-…» он пропускает и ждёт — выходит обрыв
    /// или тайм-аут.</summary>
    private static RemoteFailure FromDisconnect(DisconnectReason reason) => reason switch
    {
        DisconnectReason.NoMoreAuthenticationMethodsAvailable or DisconnectReason.IllegalUserName
            or DisconnectReason.AuthenticationCanceledByUser => RemoteFailure.Auth,
        DisconnectReason.ProtocolVersionNotSupported or DisconnectReason.KeyExchangeFailed or DisconnectReason.ProtocolError
            or DisconnectReason.MacError or DisconnectReason.CompressionError or DisconnectReason.HostKeyNotVerifiable
            or DisconnectReason.ServiceNotAvailable => RemoteFailure.Protocol,
        _ => RemoteFailure.Connect,
    };

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

    private IEnumerable<string> Secrets()
    {
        if (endpoint.Password.Length > 0) yield return endpoint.Password;
        if (endpoint.KeyPassphrase is { Length: > 0 } passphrase) yield return passphrase;
    }

    private bool Leaks(string text) => Secrets().Any(secret => text.Contains(secret, StringComparison.Ordinal));

    private string Scrub(string text) => Secrets().Aggregate(text, (t, secret) => t.Replace(secret, "***", StringComparison.Ordinal));
}

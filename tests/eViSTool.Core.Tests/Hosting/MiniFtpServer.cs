using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// Причуды тестового FTP-сервера. Готовые наборы — <see cref="GPortal"/> (как настоящий FTP G-Portal по зонду
/// 2026-10-17) и <see cref="Vsftpd"/> (как vsftpd на стенде).
/// </summary>
public sealed class MiniFtpOptions
{
    public string User { get; set; } = "vs";
    public string Password { get; set; } = "secret-пароль";
    public string Banner { get; set; } = "220 MiniFtp ready";

    /// <summary>Команды, на которые сервер отвечает «502 '&lt;cmd&gt;' not implemented» (как G-Portal).</summary>
    public HashSet<string> NotImplemented { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ответ FEAT (строки возможностей); null — FEAT не знаем вовсе.</summary>
    public string[]? Features { get; set; } = ["EPSV", "PASV", "REST STREAM", "SIZE", "MDTM", "UTF8", "MLST type*;size*;modify*;"];

    /// <summary>false — MLSD/MLST неизвестны («500 Unknown command.», как vsftpd).</summary>
    public bool Mlsd { get; set; } = true;

    /// <summary>false — REST отвергается (502), чтение с позиции невозможно.</summary>
    public bool Rest { get; set; } = true;

    /// <summary>false — переименование поверх существующего: «550 path already exists» (G-Portal).</summary>
    public bool RenameOverwrite { get; set; } = true;

    /// <summary>После прерванной клиентом передачи управляющее соединение замолкает навсегда (G-Portal).
    /// Иначе — «426», и соединение работает дальше (vsftpd).</summary>
    public bool MuteAfterAbort { get; set; }

    /// <summary>Текст ответов PASV/EPSV до скобок.</summary>
    public string PasvText { get; set; } = "Entering Passive Mode";
    public string EpsvText { get; set; } = "Entering Extended Passive Mode";

    /// <summary>Адрес, который сервер называет в PASV вместо своего (за NAT хостера).</summary>
    public string? PassiveIp { get; set; }

    /// <summary>Текст 550 на отсутствующий путь.</summary>
    public string NotFoundText { get; set; } = "550 Failed to open file.";

    /// <summary>LIST несуществующей папки — пустой список с 226 (так отвечает vsftpd), а не 550.</summary>
    public bool ListMissingAsEmpty { get; set; }

    /// <summary>Готовый текст ответа LIST для папки (сырые строки настоящего сервера).</summary>
    public Dictionary<string, string> RawList { get; } = [];

    /// <summary>LIST в стиле G-Portal («none none») или vsftpd (числовые владельцы).</summary>
    public bool ListOwnersNone { get; set; }

    /// <summary>Сертификат для AUTH TLS; null — TLS нет («502»/«500»).</summary>
    public X509Certificate2? Certificate { get; set; }

    /// <summary>Задержка перед каждым ответом.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>Сбои: команда (и часть пути в аргументе) → готовый ответ вместо обработки; ответ null — молчать;
    /// <c>Once</c> — сработать один раз.</summary>
    public List<(string Command, string? PathPart, string? Reply, bool Once)> Failures { get; } = [];

    public MiniFtpOptions Fail(string command, string? pathPart, string? reply, bool once = false)
    {
        lock (Failures) Failures.Add((command, pathPart, reply, once));
        return this;
    }

    /// <summary>
    /// Передачи RETR, которые сервер сам обрывает посередине: файл (часть пути) и сколько байт отдать до обрыва; каждая
    /// запись срабатывает один раз. С <see cref="MuteAfterAbort"/> (G-Portal) сервер закрывает после этого и управляющее
    /// соединение, без него отвечает «426» и работает дальше (vsftpd).
    /// </summary>
    public List<(string PathPart, long After)> Cuts { get; } = [];

    public MiniFtpOptions Cut(string pathPart, long after)
    {
        lock (Cuts) Cuts.Add((pathPart, after));
        return this;
    }

    public static MiniFtpOptions GPortal()
    {
        var o = new MiniFtpOptions
        {
            Banner = "220 welcome", Features = null, Mlsd = true, RenameOverwrite = false, MuteAfterAbort = true,
            PasvText = "listen socket already exists", EpsvText = "listen socket already exists",
            NotFoundText = "550 path does not exists", ListOwnersNone = true,
        };
        foreach (var c in new[] { "FEAT", "OPTS", "STAT", "NLST", "NOOP", "MDTM", "AUTH" }) o.NotImplemented.Add(c);
        return o;
    }

    public static MiniFtpOptions Vsftpd() => new()
    {
        Banner = "220 (vsFTPd 3.0.5)", Mlsd = false, ListMissingAsEmpty = true,
        Features = ["EPRT", "EPSV", "MDTM", "PASV", "REST STREAM", "SIZE", "TVFS", "UTF8"],
    };
}

/// <summary>
/// Маленький FTP-сервер для тестов адаптера: корень — временная папка, только 127.0.0.1, пассивный и активный режим,
/// AUTH TLS. Без пакетов, работает на Windows и Linux. Пишет принятые команды (пароль скрыт) в <see cref="Commands"/>.
/// </summary>
public sealed class MiniFtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<TcpClient> _clients = [];
    private readonly Task _accepting;
    private int _sessions;

    public MiniFtpOptions Options { get; }
    public string RootDir { get; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public ConcurrentQueue<string> Commands { get; } = new();

    /// <summary>Сколько управляющих соединений было открыто.</summary>
    public int Sessions => Volatile.Read(ref _sessions);

    public MiniFtpServer(MiniFtpOptions? options = null)
    {
        Options = options ?? new MiniFtpOptions();
        RootDir = Path.Combine(Path.GetTempPath(), "evistool-miniftp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootDir);
        _listener.Start();
        _accepting = AcceptLoop();
    }

    /// <summary>Положить файл в корень сервера (путь через «/»).</summary>
    public string Put(string path, byte[] data, DateTime? modifiedUtc = null)
    {
        var local = Local(path);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllBytes(local, data);
        if (modifiedUtc is { } m) File.SetLastWriteTimeUtc(local, m);
        return local;
    }

    public string Local(string path) => Path.Combine(RootDir, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Сколько раз пришла команда.</summary>
    public int Count(string verb) => Commands.Count(c => c.Split(' ')[0].Equals(verb, StringComparison.OrdinalIgnoreCase));

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _clients.Add(client);
            Interlocked.Increment(ref _sessions);
            _ = Task.Run(() => new Session(this, client).RunAsync(_stop.Token));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        foreach (var c in _clients) c.Dispose();
        try { await _accepting; } catch (Exception) { }
        for (var i = 0; i < 5; i++)
        {
            try { Directory.Delete(RootDir, recursive: true); break; }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }

    /// <summary>Самоподписанный сертификат для TLS. На Windows серверный SslStream не берёт эфемерный ключ — поэтому
    /// через выгрузку в PFX и загрузку обратно.</summary>
    public static X509Certificate2 CreateCertificate(string subject = "CN=miniftp.test")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.Exportable);
    }

    private sealed class Session(MiniFtpServer server, TcpClient client)
    {
        private readonly MiniFtpOptions _o = server.Options;
        private Stream _stream = client.GetStream();
        private string _cwd = "/";
        private bool _loggedIn, _protP, _mute, _drop, _binary = true;
        private string? _user, _renameFrom;
        private long _restart;
        private TcpListener? _passive;
        private IPEndPoint? _active;

        public async Task RunAsync(CancellationToken ct)
        {
            try
            {
                await ReplyAsync(_o.Banner);
                while (!ct.IsCancellationRequested)
                {
                    var line = await ReadLineAsync(ct);
                    if (line is null) break;
                    var space = line.IndexOf(' ');
                    var verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
                    var arg = space < 0 ? "" : line[(space + 1)..];
                    server.Commands.Enqueue(verb == "PASS" ? "PASS ***" : line);
                    if (_mute) continue; // G-Portal после обрыва передачи: соединение живо, но ответов больше нет
                    if (_o.Latency > TimeSpan.Zero) await Task.Delay(_o.Latency, ct);
                    if (Failure(verb, arg) is { } fail)
                    {
                        if (fail.Reply is { } reply) await ReplyAsync(reply);
                        continue;
                    }
                    if (verb == "QUIT") { await ReplyAsync("221 Goodbye."); break; }
                    await HandleAsync(verb, arg, ct);
                    if (_drop) break; // оборвали передачу — закрываем и управляющее соединение
                }
            }
            catch (Exception) { /* клиент ушёл — сессии конец */ }
            finally
            {
                _passive?.Stop();
                client.Dispose();
            }
        }

        private (string Command, string? PathPart, string? Reply, bool Once)? Failure(string verb, string arg)
        {
            lock (_o.Failures)
                foreach (var f in _o.Failures)
                    if (f.Command.Equals(verb, StringComparison.OrdinalIgnoreCase) && (f.PathPart is null || arg.Contains(f.PathPart, StringComparison.Ordinal)))
                    {
                        if (f.Once) _o.Failures.Remove(f);
                        return f;
                    }
            return null;
        }

        private async Task HandleAsync(string verb, string arg, CancellationToken ct)
        {
            if (_o.NotImplemented.Contains(verb)) { await ReplyAsync($"502 '{verb.ToLowerInvariant()}' not implemented"); return; }
            if (!_loggedIn && verb is not ("USER" or "PASS" or "AUTH" or "PBSZ" or "PROT" or "FEAT" or "SYST" or "OPTS"))
            {
                await ReplyAsync("530 Please login with USER and PASS.");
                return;
            }
            switch (verb)
            {
                case "USER": _user = arg; await ReplyAsync("331 Please specify the password."); break;
                case "PASS":
                    _loggedIn = _user == _o.User && arg == _o.Password;
                    await ReplyAsync(_loggedIn ? "230 Login successful." : "530 Login incorrect.");
                    break;
                case "AUTH":
                    if (_o.Certificate is null) { await ReplyAsync("500 Unknown command."); break; }
                    await ReplyAsync("234 Proceed with negotiation.");
                    var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(_o.Certificate, false, false);
                    _stream = ssl;
                    break;
                case "PBSZ": await ReplyAsync("200 PBSZ set to 0."); break;
                case "PROT": _protP = arg.Equals("P", StringComparison.OrdinalIgnoreCase); await ReplyAsync("200 PROT now " + (_protP ? "Private." : "Clear.")); break;
                case "SYST": await ReplyAsync("215 UNIX Type: L8"); break;
                case "FEAT":
                    if (_o.Features is null) { await ReplyAsync("500 Unknown command."); break; }
                    var feat = new StringBuilder("211-Features:\r\n");
                    foreach (var f in _o.Features) feat.Append(' ').Append(f).Append("\r\n");
                    if (_o.Certificate is not null) feat.Append(" AUTH TLS\r\n PBSZ\r\n PROT\r\n");
                    await ReplyAsync(feat.Append("211 End").ToString());
                    break;
                case "OPTS": await ReplyAsync("200 Always in UTF8 mode."); break;
                case "NOOP": await ReplyAsync("200 NOOP ok."); break;
                case "PWD": await ReplyAsync($"257 \"{_cwd}\" is the current directory"); break;
                case "CWD":
                    var dir = Resolve(arg);
                    if (Directory.Exists(server.Local(dir))) { _cwd = dir; await ReplyAsync("250 Directory successfully changed."); }
                    else await ReplyAsync("550 Failed to change directory.");
                    break;
                case "CDUP": _cwd = Parent(_cwd); await ReplyAsync("250 Directory successfully changed."); break;
                case "TYPE": _binary = !arg.StartsWith('A'); await ReplyAsync("200 Switching to " + (_binary ? "Binary" : "ASCII") + " mode."); break;
                case "MODE" or "STRU": await ReplyAsync("200 OK."); break;
                case "PASV": await ReplyAsync(await OpenPassiveAsync(extended: false)); break;
                case "EPSV": await ReplyAsync(await OpenPassiveAsync(extended: true)); break;
                case "PORT":
                    var p = arg.Split(',').Select(int.Parse).ToArray();
                    _active = new IPEndPoint(IPAddress.Parse($"{p[0]}.{p[1]}.{p[2]}.{p[3]}"), p[4] * 256 + p[5]);
                    await ReplyAsync("200 PORT command successful.");
                    break;
                case "EPRT":
                    var e = arg.Split(arg[0]);
                    _active = new IPEndPoint(IPAddress.Parse(e[2]), int.Parse(e[3], CultureInfo.InvariantCulture));
                    await ReplyAsync("200 EPRT command successful.");
                    break;
                case "LIST" or "NLST": await ListAsync(arg, mlsd: false, ct); break;
                case "MLSD":
                    if (!_o.Mlsd) { await ReplyAsync("500 Unknown command."); break; }
                    await ListAsync(arg, mlsd: true, ct);
                    break;
                case "MLST":
                    if (!_o.Mlsd) { await ReplyAsync("500 Unknown command."); break; }
                    var target = Resolve(arg);
                    if (Info(target) is not { } info) { await ReplyAsync(_o.NotFoundText); break; }
                    await ReplyAsync($"250-Listing {target}\r\n {Facts(info)} {target}\r\n250 End");
                    break;
                case "SIZE":
                    var sizeFile = new FileInfo(server.Local(Resolve(arg)));
                    await ReplyAsync(sizeFile.Exists ? $"213 {sizeFile.Length}" : _o.NotFoundText);
                    break;
                case "MDTM":
                    var mdtmFile = new FileInfo(server.Local(Resolve(arg)));
                    await ReplyAsync(mdtmFile.Exists ? $"213 {mdtmFile.LastWriteTimeUtc:yyyyMMddHHmmss}" : _o.NotFoundText);
                    break;
                case "REST":
                    if (!_o.Rest) { await ReplyAsync("502 'rest' not implemented"); break; }
                    _restart = long.Parse(arg, CultureInfo.InvariantCulture);
                    await ReplyAsync($"350 Restart position accepted ({_restart}).");
                    break;
                case "RETR": await RetrieveAsync(Resolve(arg), ct); break;
                case "STOR": await StoreAsync(Resolve(arg), ct); break;
                case "DELE":
                    var del = server.Local(Resolve(arg));
                    if (!File.Exists(del)) { await ReplyAsync(_o.NotFoundText); break; }
                    File.Delete(del);
                    await ReplyAsync("250 Delete operation successful.");
                    break;
                case "MKD":
                    var mk = Resolve(arg);
                    if (Directory.Exists(server.Local(mk)) || File.Exists(server.Local(mk)) || !Directory.Exists(server.Local(Parent(mk))))
                    { await ReplyAsync("550 Create directory operation failed."); break; }
                    Directory.CreateDirectory(server.Local(mk));
                    await ReplyAsync($"257 \"{mk}\" created");
                    break;
                case "RMD":
                    var rm = server.Local(Resolve(arg));
                    if (!Directory.Exists(rm) || Directory.EnumerateFileSystemEntries(rm).Any()) { await ReplyAsync("550 Remove directory operation failed."); break; }
                    Directory.Delete(rm);
                    await ReplyAsync("250 Remove directory operation successful.");
                    break;
                case "RNFR":
                    var from = Resolve(arg);
                    if (Info(from) is null) { await ReplyAsync(_o.NotFoundText); break; }
                    _renameFrom = from;
                    await ReplyAsync("350 Ready for RNTO.");
                    break;
                case "RNTO": await RenameAsync(Resolve(arg)); break;
                case "ABOR": await ReplyAsync("225 No transfer to ABOR."); break;
                case "STAT": await ReplyAsync("211 MiniFtp status OK."); break;
                default: await ReplyAsync("500 Unknown command."); break;
            }
        }

        private async Task RenameAsync(string to)
        {
            var from = _renameFrom;
            _renameFrom = null;
            if (from is null) { await ReplyAsync("503 RNFR required first."); return; }
            var src = server.Local(from);
            var dst = server.Local(to);
            if (Info(to) is not null)
            {
                if (!_o.RenameOverwrite) { await ReplyAsync("550 path already exists"); return; }
                if (File.Exists(dst)) File.Delete(dst);
            }
            if (Directory.Exists(src)) Directory.Move(src, dst); else File.Move(src, dst);
            await ReplyAsync("250 Rename successful.");
        }

        private async Task<string> OpenPassiveAsync(bool extended)
        {
            _passive?.Stop();
            _active = null;
            _passive = new TcpListener(IPAddress.Loopback, 0);
            _passive.Start(1);
            var port = ((IPEndPoint)_passive.LocalEndpoint).Port;
            if (extended) return $"229 {_o.EpsvText} (|||{port}|)";
            var ip = (_o.PassiveIp ?? "127.0.0.1").Replace('.', ',');
            return $"227 {_o.PasvText} ({ip},{port / 256},{port % 256})";
        }

        /// <summary>Соединение данных: пассивное — ждём клиента, активное — идём к нему; при PROT P — TLS.</summary>
        private async Task<(TcpClient Client, Stream Stream)?> OpenDataAsync(CancellationToken ct)
        {
            TcpClient data;
            if (_passive is { } listener)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(10));
                try { data = await listener.AcceptTcpClientAsync(wait.Token); }
                catch (OperationCanceledException) { await ReplyAsync("425 Failed to establish connection."); return null; }
                finally { listener.Stop(); _passive = null; }
            }
            else if (_active is { } ep)
            {
                data = new TcpClient();
                await data.ConnectAsync(ep, ct);
                _active = null;
            }
            else { await ReplyAsync("425 Use PORT or PASV first."); return null; }
            Stream stream = data.GetStream();
            if (_protP && _o.Certificate is not null)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(_o.Certificate, false, false);
                stream = ssl;
            }
            return (data, stream);
        }

        private async Task ListAsync(string arg, bool mlsd, CancellationToken ct)
        {
            // FluentFTP пишет «LIST -a путь» — ключи пропускаем
            var parts = arg.Split(' ', 2);
            var path = parts[0].StartsWith('-') ? (parts.Length > 1 ? parts[1] : "") : arg;
            var dir = server.Local(Resolve(path));
            if (!mlsd && _o.RawList.TryGetValue(Resolve(path), out var raw))
            {
                await ReplyAsync("150 Opening data connection.");
                if (await OpenDataAsync(ct) is not { } r) return;
                using (r.Client)
                await using (r.Stream)
                    await r.Stream.WriteAsync(Encoding.UTF8.GetBytes(raw.TrimEnd().ReplaceLineEndings("\r\n") + "\r\n"), ct); // зонд сохранил без CRLF в конце
                await ReplyAsync("226 Directory send OK.");
                return;
            }
            var exists = Directory.Exists(dir);
            if (!exists && (mlsd || !_o.ListMissingAsEmpty)) { await ReplyAsync(mlsd ? _o.NotFoundText : "550 Failed to open directory."); return; }
            var text = new StringBuilder();
            if (exists)
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal))
                    text.Append(mlsd ? $"{Facts(entry)} {entry.Name}" : UnixLine(entry)).Append("\r\n");
            await ReplyAsync(mlsd ? "150 Here comes the directory listing." : "150 Opening data connection.");
            if (await OpenDataAsync(ct) is not { } d) return;
            using (d.Client)
            await using (d.Stream)
                await d.Stream.WriteAsync(Encoding.UTF8.GetBytes(text.ToString()), ct);
            await ReplyAsync("226 Directory send OK.");
        }

        private async Task RetrieveAsync(string path, CancellationToken ct)
        {
            var local = server.Local(path);
            var offset = _restart;
            _restart = 0;
            if (!File.Exists(local)) { await ReplyAsync(_o.NotFoundText); return; }
            long? cut = null;
            lock (_o.Cuts)
            {
                var i = _o.Cuts.FindIndex(c => path.Contains(c.PathPart, StringComparison.Ordinal));
                if (i >= 0)
                {
                    cut = _o.Cuts[i].After;
                    _o.Cuts.RemoveAt(i);
                }
            }
            await ReplyAsync("150 Opening BINARY mode data connection.");
            if (await OpenDataAsync(ct) is not { } d) return;
            var aborted = false;
            var cutShort = false;
            using (d.Client)
            {
                try
                {
                    await using var file = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    file.Position = Math.Min(offset, file.Length);
                    var limit = cut is { } c ? file.Position + c : long.MaxValue;
                    var buffer = new byte[16 * 1024];
                    int n;
                    while (file.Position < limit && (n = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - file.Position)), ct)) > 0)
                        await d.Stream.WriteAsync(buffer.AsMemory(0, n), ct);
                    cutShort = file.Position < file.Length;
                    await d.Stream.FlushAsync(ct);
                    if (d.Stream is SslStream ssl) await ssl.ShutdownAsync();
                }
                catch (IOException) { aborted = true; }
                try { await d.Stream.DisposeAsync(); } catch (IOException) { aborted = true; }
            }
            if (cutShort && !aborted)
            {
                // сервер сам оборвал передачу посреди файла: G-Portal после этого закрывает и управляющее соединение
                if (_o.MuteAfterAbort) { _drop = true; return; }
                await ReplyAsync("426 Connection closed; transfer aborted.");
                return;
            }
            if (!aborted) { await ReplyAsync("226 Transfer complete."); return; }
            if (_o.MuteAfterAbort) { _mute = true; return; }
            await ReplyAsync("426 Failure writing network stream.");
        }

        private async Task StoreAsync(string path, CancellationToken ct)
        {
            var local = server.Local(path);
            if (!Directory.Exists(Path.GetDirectoryName(local))) { await ReplyAsync("553 Could not create file."); return; }
            await ReplyAsync("150 Ok to send data.");
            if (await OpenDataAsync(ct) is not { } d) return;
            using (d.Client)
            await using (d.Stream)
            await using (var file = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                await d.Stream.CopyToAsync(file, ct);
            await ReplyAsync("226 Transfer complete.");
        }

        private FileSystemInfo? Info(string path)
        {
            var local = server.Local(path);
            if (File.Exists(local)) return new FileInfo(local);
            if (Directory.Exists(local)) return new DirectoryInfo(local);
            return null;
        }

        private static string Facts(FileSystemInfo i) => i is FileInfo f
            ? $"size={f.Length};create={f.CreationTimeUtc:yyyyMMddHHmmss};modify={f.LastWriteTimeUtc:yyyyMMddHHmmss};type=file;"
            : $"size=4096;create={i.CreationTimeUtc:yyyyMMddHHmmss};modify={i.LastWriteTimeUtc:yyyyMMddHHmmss};type=dir;";

        private string UnixLine(FileSystemInfo i)
        {
            var t = i.LastWriteTimeUtc; // «местное время сервера» у стенда — UTC
            var when = t > DateTime.UtcNow.AddDays(-180)
                ? t.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture)
                : t.ToString("MMM dd  yyyy", CultureInfo.InvariantCulture);
            var size = i is FileInfo f ? f.Length : 4096;
            var mode = i is FileInfo ? "-rwxrwx---" : "drwxrwx---";
            return _o.ListOwnersNone
                ? $"{mode} 1 none none {size} {when} {i.Name}"
                : $"{mode}    1 1001     1001     {size,8} {when} {i.Name}";
        }

        private string Resolve(string arg)
        {
            var path = arg.Length == 0 ? _cwd : arg.StartsWith('/') ? arg : _cwd.TrimEnd('/') + "/" + arg;
            var parts = new List<string>();
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".") continue;
                if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(part);
            }
            return "/" + string.Join('/', parts);
        }

        private static string Parent(string path)
        {
            var i = path.TrimEnd('/').LastIndexOf('/');
            return i <= 0 ? "/" : path[..i];
        }

        private async Task ReplyAsync(string text)
        {
            await _stream.WriteAsync(Encoding.UTF8.GetBytes(text + "\r\n"));
            await _stream.FlushAsync();
        }

        /// <summary>Строка команды по байту: буфер StreamReader мешал бы переключиться на TLS после AUTH.</summary>
        private async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                if (await _stream.ReadAsync(one, ct) == 0) return bytes.Count > 0 ? Encoding.UTF8.GetString([.. bytes]) : null;
                if (one[0] == '\n') break;
                if (one[0] != '\r') bytes.Add(one[0]);
            }
            return Encoding.UTF8.GetString([.. bytes]);
        }
    }
}

using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Окно «Сервер у хостера»: адрес FTP (хост, хост:порт или целиком ftp://логин@хост:порт/папка), порт, шифрование,
/// логин и пароль, в «Дополнительно» — папка сервера и активный режим; «Проверить подключение» входит на сервер и
/// одной строкой говорит, что нашлось. Ответ окна — <see cref="Result"/>: подключение с уже зашифрованным паролем и
/// закреплёнными путями. Открытый пароль живёт только в этой модели; в строки окна и в исключения он не попадает.
/// При правке готового подключения пустое поле пароля значит «оставить прежний».
/// </summary>
public sealed partial class HostedConnectViewModel : ObservableObject
{
    /// <summary>
    /// Что сделать с моделью сразу после создания — только для снимков окна (dialog.hostedconnect.*): у снимка нет
    /// другого способа показать окно с введённым адресом и готовым итогом проверки. Берётся один раз, первой созданной моделью.
    /// </summary>
    internal static Action<HostedConnectViewModel>? SnapshotInit;

    /// <summary>Порядок пунктов в списке «Шифрование» (и в <see cref="TlsOptions"/>).</summary>
    private static readonly FtpTls[] TlsOrder = [FtpTls.Auto, FtpTls.Explicit, FtpTls.None, FtpTls.Implicit];

    private readonly HostedConnection? _existing;
    private readonly Func<HostedEndpoint, HostedConnection, CancellationToken, Task<HostedCheckResult>> _check;
    // проверка, которая идёт сейчас: поля поменяли — её отменяют, и её ответ окно уже не покажет
    private CancellationTokenSource? _checkCts;
    // раскладка из последней удачной проверки; любая правка полей её сбрасывает, закрепляется только свежая
    private HostedLayout? _checkedLayout;
    private string? _checkedVersion; // версия игры из той же проверки, что и _checkedLayout
    // сертификат, которому человек доверился (или был в подключении); null — не доверяем никакому
    private string? _trustedCertificate;
    private string? _pendingCertificate;
    private HostedProtocol _protocol = HostedProtocol.Ftp;
    // поля заполняет сама модель (загрузка, разбор адреса, итог проверки): это не правка человека
    private bool _quiet;
    // поля, которые сейчас заполнены из ссылки в адресе: что в поле было до неё и что в него положила ссылка.
    // Убрали часть из ссылки — поле возвращается к прежнему, если человек его с тех пор не трогал
    private readonly Dictionary<string, (object Before, object Applied)> _fromLink = [];

    /// <param name="existing">Готовое подключение (его правят) или null (создают новое). Модель его не меняет.</param>
    /// <param name="okText">Надпись на кнопке «ОК»: «Подключить», «Сохранить».</param>
    /// <param name="check">Проверка подключения; по умолчанию — настоящая <see cref="HostedProbe.CheckAsync"/> (тесты подменяют).</param>
    public HostedConnectViewModel(HostedConnection? existing, string okText,
        Func<HostedEndpoint, HostedConnection, CancellationToken, Task<HostedCheckResult>>? check = null)
    {
        _existing = existing?.Clone();
        OkText = okText;
        _check = check ?? HostedProbe.CheckAsync;
        TlsOptions =
        [
            Loc.T("hosted.tlsAuto"), Loc.T("hosted.tlsExplicit"), Loc.T("hosted.tlsNone"), Loc.T("hosted.tlsImplicit"),
        ];
        _quiet = true;
        if (_existing is { } e)
        {
            Address = e.Protocol == HostedProtocol.Sftp ? "sftp://" + e.Host : e.Host;
            PortText = e.Port.ToString(CultureInfo.InvariantCulture);
            TlsIndex = Math.Max(0, Array.IndexOf(TlsOrder, e.Tls));
            User = e.User;
            Root = string.IsNullOrEmpty(e.Root) ? "/" : e.Root;
            ActiveMode = e.ActiveMode;
            _trustedCertificate = e.CertificateSha256;
            // свёрнутое «Дополнительно» не должно прятать то, что человек уже менял
            IsAdvancedOpen = e.ActiveMode || Root != "/";
        }
        _quiet = false;
        Reparse();
        if (Interlocked.Exchange(ref SnapshotInit, null) is { } init) init(this);
    }

    public string OkText { get; }

    /// <summary>Пункты списка «Шифрование», в порядке <see cref="TlsIndex"/>.</summary>
    public IReadOnlyList<string> TlsOptions { get; }

    /// <summary>Правка готового подключения с сохранённым паролем: под полем «оставь пустым, чтобы не менять».</summary>
    public bool HasSavedPassword => _existing?.Password is not null;

    // ===== поля =====

    /// <summary>Адрес, как его ввели: хост, хост:порт или ftp://логин@хост:порт/папка. Разбирается на лету.</summary>
    [ObservableProperty] private string _address = "";

    /// <summary>Порт как текст (поле ввода); число — <see cref="Port"/>.</summary>
    [ObservableProperty] private string _portText = "21";

    /// <summary>Номер пункта списка «Шифрование» (<see cref="TlsOptions"/>).</summary>
    [ObservableProperty] private int _tlsIndex;

    [ObservableProperty] private string _user = "";

    /// <summary>Пароль открытым текстом (поле в окне скрытое); пустой при правке — оставить прежний.</summary>
    [ObservableProperty] private string _password = "";

    /// <summary>Папка сервера на FTP; «/» — корень.</summary>
    [ObservableProperty] private string _root = "/";

    /// <summary>Активный режим FTP (PORT) вместо пассивного: на случай, когда соединение виснет на списке файлов.</summary>
    [ObservableProperty] private bool _activeMode;

    /// <summary>Раскрыт блок «Дополнительно».</summary>
    [ObservableProperty] private bool _isAdvancedOpen;

    // ===== разобранное =====

    /// <summary>Хост из адреса (без схемы, порта и папки); пусто — адрес пустой или не разобрался.</summary>
    public string Host { get; private set; } = "";

    /// <summary>Порт; null — в поле не число от 1 до 65535.</summary>
    public int? Port => int.TryParse(PortText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 65535 ? p : null;

    public FtpTls Tls => TlsOrder[Math.Clamp(TlsIndex, 0, TlsOrder.Length - 1)];

    /// <summary>Адрес — ссылка sftp://: этот протокол появится позже, подключить и проверить нельзя.</summary>
    public bool IsSftp => _protocol == HostedProtocol.Sftp;

    /// <summary>Папка сервера в порядке (нормализованный путь); null — в ней «..» или управляющие знаки.</summary>
    public string? NormalizedRoot => NormalizeRoot(Root);

    /// <summary>Пояснение под адресом: формат; для sftp:// — «SFTP появится в следующей версии»; для неразобранного — формат красным.</summary>
    [ObservableProperty] private string _addressHint = "";
    [ObservableProperty] private RowTone _addressTone = RowTone.Muted;

    /// <summary>Пояснение под портом (21, 8821, SFTP, 990); для остальных портов пусто.</summary>
    [ObservableProperty] private string _portHint = "";
    [ObservableProperty] private RowTone _portTone = RowTone.Muted;

    /// <summary>Пояснение под папкой сервера; красным, если папка неверная.</summary>
    [ObservableProperty] private string _rootHint = "";
    [ObservableProperty] private RowTone _rootTone = RowTone.Muted;

    /// <summary>«ОК» доступна: хост разобран, порт — число, папка верная, протокол не SFTP.</summary>
    public bool CanAccept => Host.Length > 0 && Port is not null && NormalizedRoot is not null && !IsSftp;

    // ===== проверка =====

    /// <summary>Итог проверки одной строкой: «Проверяю…», «Вход выполнен…» или ошибка словами.</summary>
    [ObservableProperty] private string _checkText = "";
    [ObservableProperty] private RowTone _checkTone = RowTone.Muted;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    private bool _isChecking;

    /// <summary>Сервер показал сертификат, которому мы не доверяем: текст с отпечатком и кнопка «Доверять».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingCertificate))]
    private string _certificateText = "";

    public bool HasPendingCertificate => _pendingCertificate is not null;

    /// <summary>Ответ окна по «ОК»; null — ещё не нажимали.</summary>
    public HostedConnection? Result { get; private set; }

    // ===== правки полей =====

    partial void OnAddressChanged(string value) => Reparse();
    partial void OnPortTextChanged(string value) => Edited();
    partial void OnTlsIndexChanged(int value) => Edited();
    partial void OnUserChanged(string value) => Edited();
    partial void OnPasswordChanged(string value) => Edited();
    partial void OnRootChanged(string value) => Edited();
    partial void OnActiveModeChanged(bool value) => Edited();

    /// <summary>
    /// Разобрать адрес: хост, а из ссылки ещё порт, логин, пароль, папку и шифрование — те части, что в ней есть.
    /// Поля заполняются, но сам адрес по ходу набора не переписывается (иначе человек не допишет ссылку);
    /// это делает <see cref="CommitAddress"/>, когда он ушёл из поля.
    /// </summary>
    private void Reparse()
    {
        var parsed = ParseAddress(Address);
        var oldHost = Host;
        Host = parsed?.Host ?? "";
        _protocol = parsed?.Protocol ?? HostedProtocol.Ftp;
        if (parsed is { } p && !_quiet)
        {
            _quiet = true; // поля заполняются из ссылки: каждое из них не должно отдельно сбрасывать проверку
            try
            {
                var port = p.Port?.ToString(CultureInfo.InvariantCulture) ?? (p.Scheme == "ftps" ? "990" : null);
                FromLink(nameof(PortText), port, PortText, v => PortText = (string)v);
                FromLink(nameof(TlsIndex), p.Scheme == "ftps" ? Array.IndexOf(TlsOrder, FtpTls.Implicit) : null, TlsIndex, v => TlsIndex = (int)v);
                FromLink(nameof(User), p.User is { Length: > 0 } ? p.User : null, User, v => User = (string)v);
                FromLink(nameof(Password), p.Password is { Length: > 0 } ? p.Password : null, Password, v => Password = (string)v);
                var path = p.Path is { Length: > 0 } and not "/" ? p.Path : null;
                FromLink(nameof(Root), path, Root, v => Root = (string)v);
                if (path is not null) IsAdvancedOpen = true;
            }
            finally { _quiet = false; }
        }
        // доверие к сертификату — к серверу, который был; другой хост — другой сертификат
        if (!_quiet && !string.Equals(oldHost, Host, StringComparison.OrdinalIgnoreCase)) _trustedCertificate = null;
        Edited();
    }

    /// <summary>
    /// Одно поле из ссылки: часть есть — положить её в поле (и запомнить, что там было до ссылки); части больше нет —
    /// вернуть прежнее, если в поле всё ещё то, что положила ссылка (своё человек правил сам — его не трогаем).
    /// </summary>
    private void FromLink(string field, object? value, object current, Action<object> set)
    {
        if (value is not null)
        {
            var before = _fromLink.TryGetValue(field, out var was) ? was.Before : current;
            _fromLink[field] = (before, value);
            set(value);
        }
        else if (_fromLink.Remove(field, out var was) && Equals(current, was.Applied)) set(was.Before);
    }

    /// <summary>
    /// Человек ушёл из поля адреса (или нажал «Проверить»/«ОК»): из ссылки остаётся один хост, остальное уже лежит в
    /// своих полях и дальше принадлежит им. У ссылки sftp:// остаётся схема (иначе пропадёт объяснение, почему подключить
    /// нельзя), но логин и пароль из видимого поля уходят и у неё.
    /// </summary>
    public void CommitAddress()
    {
        if (Host.Length == 0) return;
        var commit = IsSftp ? "sftp://" + Host : Host;
        if (Address.Trim() == commit) return;
        var keep = _checkedLayout;
        _fromLink.Clear(); // поля больше не «из ссылки»: адрес станет голым хостом, а заполненное остаётся
        Address = commit;
        _checkedLayout = keep; // хост тот же — проверка по-прежнему про это подключение
    }

    private void Edited()
    {
        if (!_quiet)
        {
            CancelCheck();
            CheckText = ""; // итог прошлой проверки — про прежние поля
            _checkedLayout = null;
            SetPending(null);
        }
        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(Tls));
        OnPropertyChanged(nameof(IsSftp));
        OnPropertyChanged(nameof(NormalizedRoot));
        OnPropertyChanged(nameof(CanAccept));
        CheckCommand.NotifyCanExecuteChanged();
        UpdateHints();
    }

    private void UpdateHints()
    {
        (AddressHint, AddressTone) =
            IsSftp ? (Loc.T("hosted.sftpLater"), RowTone.Warn)
            : Address.Trim().Length > 0 && Host.Length == 0 ? (Loc.T("hosted.hostHint"), RowTone.Danger)
            : (Loc.T("hosted.hostHint"), RowTone.Muted);

        (PortHint, PortTone) = Port switch
        {
            21 => (Loc.T("hosted.portHintFtp"), RowTone.Muted),
            8821 => (Loc.T("hosted.portHintTcadmin"), RowTone.Muted),
            22 or 2022 or 8822 => (Loc.T("hosted.portSftp", Port), RowTone.Warn),
            990 => (Loc.T("hosted.portImplicit"), RowTone.Muted),
            _ => ("", RowTone.Muted),
        };

        (RootHint, RootTone) = NormalizedRoot is null ? (Loc.T("hosted.rootHint"), RowTone.Danger) : (Loc.T("hosted.rootHint"), RowTone.Muted);
    }

    /// <summary>Папка сервера: пусто и «/» — корень; иначе нормализованный путь; «..» и управляющие знаки — null.</summary>
    private static string? NormalizeRoot(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return "/";
        try { return RemotePath.Normalize(trimmed); }
        catch (InvalidDataException) { return null; }
    }

    // ===== проверка подключения =====

    private bool CanCheck => CanAccept && !IsChecking;

    /// <summary>
    /// Войти на сервер и сказать, что нашлось. Пока ждём, поля могут поменять: тогда проверку отменяют и её ответ не
    /// показывают — он уже не про те поля. Поэтому команда допускает новый запуск, не дожидаясь старого.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheck), AllowConcurrentExecutions = true)]
    private async Task CheckAsync()
    {
        if (!CanAccept) return;
        CommitAddress();
        CancelCheck();
        SetPending(null);
        _checkedLayout = null;
        if (EndpointPassword() is not { } password)
        {
            // сохранённый пароль не расшифровать (другой компьютер или пользователь): пустым его не шлём — сервер ответил бы
            // «неверный пароль», а дело не в нём
            CheckTone = RowTone.Danger;
            CheckText = Loc.T("hosted.passwordUnreadable");
            return;
        }
        _checkCts = new CancellationTokenSource();
        var ct = _checkCts.Token;
        IsChecking = true;
        CheckTone = RowTone.Muted;
        CheckText = Loc.T("hosted.checking");
        string result;
        var ok = false;
        HostedCheckResult? done = null;
        try
        {
            var settings = BuildConnection();
            var endpoint = BuildEndpoint(password);
            done = await _check(endpoint, settings, ct);
            result = CheckSummary(done);
            ok = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // поля поменяли или окно закрыли
        }
        catch (RemoteFilesException ex) when (ex is { Kind: RemoteFailure.CertificateUntrusted, CertificateSha256.Length: > 0 })
        {
            if (ct.IsCancellationRequested) return;
            _checkCts = null;
            SetPending(ex.CertificateSha256, HostedSecret.Describe(ex));
            CheckText = "";
            IsChecking = false;
            return;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException or TaskCanceledException
                                       or TimeoutException or UnauthorizedAccessException)
        {
            // отмена не наша (её ловит ветка выше) — у транспорта вышло время ожидания; свой текст у неё по-английски
            result = ex is OperationCanceledException ? Loc.T("hosted.errTimeout") : HostedSecret.Describe(ex);
        }
        if (ct.IsCancellationRequested) return; // ответ пришёл, но поля уже другие
        _checkCts = null;
        if (ok && done is not null)
        {
            _checkedLayout = done.Layout;
            _checkedVersion = done.GameVersion?.ToString();
            // без тихого понижения: сервер умеет TLS — «если сервер умеет» становится «только с шифрованием»
            if (Tls == FtpTls.Auto && done.Features.Encrypted)
            {
                _quiet = true;
                try { TlsIndex = Array.IndexOf(TlsOrder, FtpTls.Explicit); }
                finally { _quiet = false; }
                OnPropertyChanged(nameof(Tls));
            }
        }
        CheckText = result;
        CheckTone = ok ? RowTone.Good : RowTone.Danger;
        IsChecking = false;
    }

    /// <summary>Итог проверки одной строкой: «Вход выполнен, без шифрования. Конфиг: … Моды: … Игра: ….».</summary>
    internal static string CheckSummary(HostedCheckResult r) =>
        Loc.T("hosted.checkOk", r.Features.Encrypted ? Loc.T("hosted.encrypted") : Loc.T("hosted.noEncryption"),
            r.Layout.ConfigPath ?? Loc.T("common.notFoundM"), ModsText(r), GameText(r));

    private static string ModsText(HostedCheckResult r) =>
        r.Layout.ModDirs.Count == 0 ? Loc.T("common.notFoundM") : $"{string.Join(", ", r.Layout.ModDirs)} ({r.ModCount})";

    private static string GameText(HostedCheckResult r)
    {
        if (r.GameVersion is not { } version) return Loc.T("common.notFound");
        var source = r.VersionSource switch
        {
            GameVersionSource.Manual => Loc.T("hosted.versionSourceManual"),
            GameVersionSource.VersionFile => Loc.T("hosted.versionSourceFile"),
            GameVersionSource.LastLaunch => Loc.T("hosted.versionSourceLastLaunch"),
            GameVersionSource.ServerLog => Loc.T("hosted.versionSourceLog"),
            GameVersionSource.ServerDll => Loc.T("hosted.versionSourceDll"),
            _ => null,
        };
        return source is null ? version.ToString() : $"{version} ({source})";
    }

    /// <summary>«Доверять этому сертификату»: отпечаток запоминается в подключении, проверка идёт заново.</summary>
    [RelayCommand]
    private async Task TrustCertificateAsync()
    {
        if (_pendingCertificate is not { } sha) return;
        _trustedCertificate = sha;
        SetPending(null);
        await CheckAsync();
    }

    /// <summary>Показать запрос о сертификате — только для снимка окна (dialog.hostedconnect.cert).</summary>
    internal void ShowCertificatePrompt(string sha) =>
        SetPending(sha, HostedSecret.Describe(new RemoteFilesException(RemoteFailure.CertificateUntrusted, "untrusted") { CertificateSha256 = sha }));

    private void SetPending(string? sha, string text = "")
    {
        _pendingCertificate = sha;
        CertificateText = sha is null ? "" : text;
        OnPropertyChanged(nameof(HasPendingCertificate));
    }

    /// <summary>Бросить идущую проверку (поля поменяли, окно закрыли): её ответ уже не нужен, «Проверяю…» убирается.</summary>
    public void CancelCheck()
    {
        if (_checkCts is not { } cts) return;
        _checkCts = null;
        cts.Cancel(); // без Dispose: таймера у источника нет, а токен ещё может держать запрос, который сейчас отменяется
        IsChecking = false;
        CheckText = "";
    }

    // ===== ответ =====

    /// <summary>
    /// «ОК»: собрать подключение (пароль шифруется здесь), закрепить пути из удачной проверки и отдать окну.
    /// null — «ОК» сейчас недоступна.
    /// </summary>
    public HostedConnection? Accept()
    {
        if (!CanAccept) return null;
        CommitAddress();
        CancelCheck();
        var connection = BuildConnection();
        Result = _checkedLayout is { } layout ? HostedProbe.Pin(connection, layout) : connection;
        if (_checkedLayout is not null && _checkedVersion is not null) Result.FoundGameVersion = _checkedVersion;
        return Result;
    }

    /// <summary>Подключение из полей окна. Остальное (версия игры, «не напоминать», закреплённые пути) — от прежнего.</summary>
    private HostedConnection BuildConnection()
    {
        var c = _existing?.Clone() ?? new HostedConnection();
        var root = NormalizedRoot ?? "/";
        // другой сервер или другая папка: закреплённые пути старого уже не про него, раскладка найдётся заново
        if (_existing is { } e && (!string.Equals(e.Host, Host, StringComparison.OrdinalIgnoreCase) || NormalizeRoot(e.Root) != root))
            (c.ConfigPath, c.ModDirs, c.InstallDir, c.GameDir) = (null, null, null, null);
        c.Protocol = HostedProtocol.Ftp;
        c.Host = Host;
        c.Port = Port ?? 21;
        c.Tls = Tls;
        c.User = User.Trim();
        c.ActiveMode = ActiveMode;
        c.Root = root;
        c.CertificateSha256 = _trustedCertificate;
        if (Password.Length > 0) c.Password = HostedSecret.Protect(Password);
        return c;
    }

    /// <summary>
    /// Пароль для проверки: введённый, а при пустом поле — сохранённый (расшифрованный) или пустой, если его нет.
    /// null — сохранённый есть, но его не расшифровать.
    /// </summary>
    private string? EndpointPassword() =>
        Password.Length > 0 ? Password
        : _existing?.Password is { Length: > 0 } saved ? HostedSecret.Unprotect(saved)
        : "";

    /// <summary>Подключение с расшифрованным паролем — для одной проверки.</summary>
    private HostedEndpoint BuildEndpoint(string password) =>
        new(HostedProtocol.Ftp, Host, Port ?? 21, Tls, User.Trim(), password, ActiveMode, _trustedCertificate);

    // ===== разбор адреса =====

    /// <summary>Что нашлось в адресе. Порт, логин, пароль и папка — null, если их в адресе не было.</summary>
    /// <param name="Scheme">«ftp», «ftps» или «sftp»; у адреса без схемы — «ftp».</param>
    public sealed record AddressParts(string Scheme, string Host, int? Port, string? User, string? Password, string? Path)
    {
        public HostedProtocol Protocol => Scheme == "sftp" ? HostedProtocol.Sftp : HostedProtocol.Ftp;

        /// <summary>Без пароля: ToString, созданный записью, напечатал бы все поля, и пароль из ссылки ушёл бы в журнал.</summary>
        public override string ToString() =>
            $"AddressParts {{ Scheme = {Scheme}, Host = {Host}, Port = {Port}, User = {User}, Password = {(Password is null ? "" : "***")}, Path = {Path} }}";
    }

    /// <summary>
    /// Разобрать адрес: <c>host</c>, <c>host:port</c>, <c>ftp://логин@host:port/путь</c>, то же с <c>ftps://</c> и <c>sftp://</c>
    /// (логин и пароль в ссылке — в процентной записи). null — пусто или не адрес.
    /// </summary>
    public static AddressParts? ParseAddress(string? text)
    {
        var s = text?.Trim() ?? "";
        if (s.Length == 0 || s.Any(char.IsWhiteSpace)) return null;

        var scheme = "ftp";
        var rest = s;
        if (s.IndexOf("://", StringComparison.Ordinal) is var at and >= 0)
        {
            scheme = s[..at].ToLowerInvariant();
            if (scheme is not ("ftp" or "ftps" or "sftp")) return null;
            rest = s[(at + 3)..];
        }

        var slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? null : Unescape(rest[slash..]);

        string? user = null, password = null;
        var userInfoEnd = authority.LastIndexOf('@');
        if (userInfoEnd >= 0)
        {
            var info = authority[..userInfoEnd];
            authority = authority[(userInfoEnd + 1)..];
            var colon = info.IndexOf(':');
            user = Unescape(colon < 0 ? info : info[..colon]);
            if (colon >= 0) password = Unescape(info[(colon + 1)..]);
        }

        string host;
        string? portText = null;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0) return null;
            host = authority[1..close];
            if (!IsIPv6(host)) return null; // в скобках бывает только IPv6
            var tail = authority[(close + 1)..];
            if (tail.Length > 0)
            {
                if (tail[0] != ':') return null;
                portText = tail[1..];
            }
        }
        else if (authority.Count(c => c == ':') == 1)
        {
            var colon = authority.IndexOf(':');
            host = authority[..colon];
            portText = authority[(colon + 1)..];
        }
        else
        {
            host = authority; // без порта
            // двоеточий больше одного без скобок — только голый IPv6; «203.0.113.40:21:5» — опечатка, а не хост
            if (host.Contains(':') && !IsIPv6(host)) return null;
        }

        if (!host.Contains(':') && !IsHostName(host)) return null;
        int? port = null;
        if (!string.IsNullOrEmpty(portText))
        {
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535) return null;
            port = p;
        }
        return new AddressParts(scheme, host.ToLowerInvariant(), port, user, password, path);
    }

    private static string Unescape(string text)
    {
        try { return Uri.UnescapeDataString(text); }
        catch (UriFormatException) { return text; }
    }

    private static bool IsHostName(string host) =>
        host.Length > 0 && host[0] is not ('.' or '-') && host.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');

    private static bool IsIPv6(string host) =>
        IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;
}

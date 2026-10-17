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
/// Окно «Сервер у хостера»: адрес (хост, хост:порт или целиком ftp://логин@хост:порт/папка, а для SFTP —
/// sftp://логин@хост:порт/папка), порт, шифрование FTP, логин и пароль, в «Дополнительно» — папка сервера и активный
/// режим FTP; «Проверить подключение» входит на сервер и одной строкой говорит, что нашлось. У SFTP вместо шифрования —
/// способ входа: пароль или файл ключа на этом компьютере (с необязательным паролем ключа), а ключ сервера при первом
/// подключении подтверждает человек, как сертификат FTPS. Ответ окна — <see cref="Result"/>: подключение с уже
/// зашифрованными паролями и закреплёнными путями. Открытые пароли живут только в этой модели; в строки окна и в
/// исключения они не попадают. При правке готового подключения пустое поле пароля значит «оставить прежний».
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
    // ключ сервера SFTP, которому доверились, и для какого хоста и порта: на другом порту того же хоста — другой sshd
    private string? _trustedHostKey;
    private (string Host, int Port)? _trustedHostKeyFor;
    private bool _pendingIsHostKey;
    private HostedProtocol _protocol = HostedProtocol.Ftp;
    // поля заполняет сама модель (загрузка, разбор адреса, итог проверки): это не правка человека
    private bool _quiet;
    // поля, которые сейчас заполнены из ссылки в адресе: что в поле было до неё и что в него положила ссылка.
    // Убрали часть из ссылки — поле возвращается к прежнему, если человек его с тех пор не трогал
    private readonly Dictionary<string, (object Before, object Applied)> _fromLink = [];
    // порт в поле без того, что положила ссылка (её «прежний»), задан для SFTP: загружен из профиля SFTP, вписан руками
    // при адресе sftp:// или остался от ссылки sftp://. Такому порту ссылка sftp:// без порта 22 не навязывает
    private bool _portIsSftp;
    // файл ключа, который уже искали на диске, и нашёлся ли он: правка других полей диск не трогает
    private (string Path, bool Found)? _keyFileSeen;

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
        AuthOptions = [Loc.T("hosted.authPassword"), Loc.T("hosted.authKey")];
        _quiet = true;
        if (_existing is { } e)
        {
            Address = e.Protocol == HostedProtocol.Sftp ? "sftp://" + e.Host : e.Host;
            PortText = e.Port.ToString(CultureInfo.InvariantCulture);
            _portIsSftp = e.Protocol == HostedProtocol.Sftp;
            TlsIndex = Math.Max(0, Array.IndexOf(TlsOrder, e.Tls));
            User = e.User;
            Root = string.IsNullOrEmpty(e.Root) ? "/" : e.Root;
            ActiveMode = e.ActiveMode;
            _trustedCertificate = e.CertificateSha256;
            if (e.HostKeySha256 is { } hostKey) (_trustedHostKey, _trustedHostKeyFor) = (hostKey, (e.Host.Trim(), e.Port));
            KeyFile = e.KeyFile ?? "";
            AuthIndex = e.KeyFile is null ? 0 : 1;
            // свёрнутое «Дополнительно» не должно прятать то, что человек уже менял
            IsAdvancedOpen = e.ActiveMode && e.Protocol != HostedProtocol.Sftp || Root != "/";
        }
        // разобрать загруженный адрес, ничего не беря из него в поля: у sftp:// без порта иначе встал бы 22 поверх сохранённого
        Reparse();
        _quiet = false;
        if (Interlocked.Exchange(ref SnapshotInit, null) is { } init) init(this);
    }

    public string OkText { get; }

    /// <summary>Пункты списка «Шифрование», в порядке <see cref="TlsIndex"/>.</summary>
    public IReadOnlyList<string> TlsOptions { get; }

    /// <summary>Пункты списка «Вход» у SFTP, в порядке <see cref="AuthIndex"/>: по паролю, по файлу ключа.</summary>
    public IReadOnlyList<string> AuthOptions { get; }

    /// <summary>Правка готового подключения с сохранённым паролем: под полем «оставь пустым, чтобы не менять».</summary>
    public bool HasSavedPassword => _existing?.Password is not null;

    /// <summary>То же для пароля ключа SFTP.</summary>
    public bool HasSavedKeyPassphrase => _existing?.KeyPassphrase is not null;

    /// <summary>Под полем пароля — «оставь пустым…»: пароль сохранён, и вход сейчас по паролю.</summary>
    public bool ShowPasswordKeep => HasSavedPassword && !IsKeyLogin;

    /// <summary>Под полем пароля ключа: «оставь пустым…», если он сохранён, иначе — что он нужен не всегда.</summary>
    public string KeyPassphraseNote => HasSavedKeyPassphrase ? Loc.T("hosted.keyPassphraseKeep") : Loc.T("hosted.keyPassphraseHint");

    /// <summary>Выбор файла ключа («Обзор…»); null — передумали. Тесты подменяют.</summary>
    public Func<string?> PickKeyFile { get; init; } = () =>
        Ui.Platform.PickFiles(Loc.T("hosted.keyPick"), [new FileFilter(Loc.T("hosted.keyFilter"), "*")], multiple: false)?.FirstOrDefault();

    /// <summary>Есть ли файл ключа на этом компьютере; тесты подменяют, чтобы считать походы на диск.</summary>
    internal Func<string, bool> KeyFileProbe { get; init; } = KeyFileExists;

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

    /// <summary>SFTP: номер пункта списка «Вход» (<see cref="AuthOptions"/>): 0 — по паролю, 1 — по файлу ключа.</summary>
    [ObservableProperty] private int _authIndex;

    /// <summary>SFTP: путь к файлу закрытого ключа на этом компьютере.</summary>
    [ObservableProperty] private string _keyFile = "";

    /// <summary>SFTP: пароль ключа открытым текстом (поле скрытое); пустой при правке — оставить прежний.</summary>
    [ObservableProperty] private string _keyPassphrase = "";

    /// <summary>Раскрыт блок «Дополнительно».</summary>
    [ObservableProperty] private bool _isAdvancedOpen;

    // ===== разобранное =====

    /// <summary>Хост из адреса (без схемы, порта и папки); пусто — адрес пустой или не разобрался.</summary>
    public string Host { get; private set; } = "";

    /// <summary>Порт; null — в поле не число от 1 до 65535.</summary>
    public int? Port => ParsePort(PortText);

    private static int? ParsePort(string text) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 65535 ? p : null;

    /// <summary>Порты, которые обычно у SFTP: 22, 2022 (Pterodactyl, Pelican), 8822. Под FTP на них — подсказка про sftp://.</summary>
    private static bool IsSftpPort(int? port) => port is 22 or 2022 or 8822;

    public FtpTls Tls => TlsOrder[Math.Clamp(TlsIndex, 0, TlsOrder.Length - 1)];

    /// <summary>Адрес — ссылка sftp://: файлы по SSH. Шифрование всегда, полей TLS и активного режима у него нет, зато
    /// есть выбор входа (пароль или ключ).</summary>
    public bool IsSftp => _protocol == HostedProtocol.Sftp;

    /// <summary>Вход по файлу ключа: адрес SFTP и в списке «Вход» выбран ключ.</summary>
    public bool IsKeyLogin => IsSftp && AuthIndex == 1;

    /// <summary>Порт, если поле не разобралось: 22 у SFTP, 21 у FTP.</summary>
    private int DefaultPort => IsSftp ? 22 : 21;

    /// <summary>Папка сервера в порядке (нормализованный путь); null — в ней «..» или управляющие знаки.</summary>
    public string? NormalizedRoot => NormalizeRoot(Root);

    /// <summary>Пояснение под адресом: формат; для sftp:// — что это SFTP; для неразобранного — формат красным.</summary>
    [ObservableProperty] private string _addressHint = "";
    [ObservableProperty] private RowTone _addressTone = RowTone.Muted;

    /// <summary>Пояснение под портом (21, 8821, 22 и порты SFTP, 990); для остальных портов пусто.</summary>
    [ObservableProperty] private string _portHint = "";
    [ObservableProperty] private RowTone _portTone = RowTone.Muted;

    /// <summary>Пояснение под папкой сервера; красным, если папка неверная.</summary>
    [ObservableProperty] private string _rootHint = "";
    [ObservableProperty] private RowTone _rootTone = RowTone.Muted;

    /// <summary>Пояснение под файлом ключа; красным, если такого файла на этом компьютере нет.</summary>
    [ObservableProperty] private string _keyFileHint = "";
    [ObservableProperty] private RowTone _keyFileTone = RowTone.Muted;

    /// <summary>«ОК» доступна: хост разобран, порт — число, папка верная, а для входа по ключу указан файл ключа.</summary>
    public bool CanAccept => Host.Length > 0 && Port is not null && NormalizedRoot is not null && (!IsKeyLogin || KeyFile.Trim().Length > 0);

    // ===== проверка =====

    /// <summary>Итог проверки одной строкой: «Проверяю…», «Вход выполнен…» или ошибка словами.</summary>
    [ObservableProperty] private string _checkText = "";
    [ObservableProperty] private RowTone _checkTone = RowTone.Muted;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    private bool _isChecking;

    /// <summary>Сервер показал сертификат (или ключ SSH), которому мы не доверяем: текст с отпечатком и кнопка «Доверять».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingCertificate))]
    private string _certificateText = "";

    public bool HasPendingCertificate => _pendingCertificate is not null;

    /// <summary>Надпись на кнопке «Доверять…»: сертификату TLS или ключу сервера SSH.</summary>
    [ObservableProperty] private string _trustText = "";

    /// <summary>Ответ окна по «ОК»; null — ещё не нажимали.</summary>
    public HostedConnection? Result { get; private set; }

    // ===== правки полей =====

    partial void OnAddressChanged(string value) => Reparse();

    partial void OnPortTextChanged(string value)
    {
        if (!_quiet)
        {
            // порт вписан руками: теперь он «прежний» для ссылки (убрали порт из ссылки — вернётся он), и задан он для
            // того протокола, что сейчас в адресе
            _fromLink.Remove(nameof(PortText));
            _portIsSftp = IsSftp;
        }
        Edited();
    }

    partial void OnTlsIndexChanged(int value) => Edited();
    partial void OnUserChanged(string value) => Edited();
    partial void OnPasswordChanged(string value) => Edited();
    partial void OnRootChanged(string value) => Edited();
    partial void OnActiveModeChanged(bool value) => Edited();

    partial void OnAuthIndexChanged(int value)
    {
        _keyFileSeen = null; // выбрали вход по ключу заново — файл могли положить или убрать, пока окно открыто
        Edited();
    }

    partial void OnKeyFileChanged(string value) => Edited();
    partial void OnKeyPassphraseChanged(string value) => Edited();

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
                var port = p.Port?.ToString(CultureInfo.InvariantCulture) ?? p.Scheme switch
                {
                    "ftps" => "990",
                    "sftp" when NeedsSftpPort() => "22",
                    _ => null,
                };
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
    /// Ссылке sftp:// без порта нужен порт 22: прежний порт (тот, что в поле без частей ссылки) задан ещё для FTP и на
    /// порт SFTP не похож. Порт из профиля SFTP, вписанный руками при sftp:// или похожий на порт SFTP (2022 у Pterodactyl)
    /// остаётся: у sftp:// схема живёт в поле, адрес после каждой правки разбирается заново, и иначе любая опечатка в хосте
    /// молча меняла бы порт на 22.
    /// </summary>
    private bool NeedsSftpPort()
    {
        if (_portIsSftp) return false;
        var own = _fromLink.TryGetValue(nameof(PortText), out var link) ? (string)link.Before : PortText;
        return !IsSftpPort(ParsePort(own));
    }

    /// <summary>
    /// Человек ушёл из поля адреса (или нажал «Проверить»/«ОК»): из ссылки остаётся один хост, остальное уже лежит в
    /// своих полях и дальше принадлежит им. У ссылки sftp:// остаётся схема — по ней выбран протокол, — но логин и
    /// пароль из видимого поля уходят и у неё.
    /// </summary>
    public void CommitAddress()
    {
        if (Host.Length == 0) return;
        var commit = IsSftp ? "sftp://" + Host : Host;
        if (Address.Trim() == commit) return;
        // поля больше не «из ссылки»: адрес станет голым хостом, а заполненное остаётся. Порт из ссылки — порт того
        // протокола, что в ней
        if (_fromLink.ContainsKey(nameof(PortText))) _portIsSftp = IsSftp;
        _fromLink.Clear();
        // тихо: хост тот же — проверка по-прежнему про это подключение, а поля из ссылки уже на своих местах
        _quiet = true;
        try { Address = commit; }
        finally { _quiet = false; }
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
        OnPropertyChanged(nameof(IsKeyLogin));
        OnPropertyChanged(nameof(ShowPasswordKeep));
        OnPropertyChanged(nameof(NormalizedRoot));
        OnPropertyChanged(nameof(CanAccept));
        CheckCommand.NotifyCanExecuteChanged();
        UpdateHints();
    }

    private void UpdateHints()
    {
        (AddressHint, AddressTone) =
            Address.Trim().Length > 0 && Host.Length == 0 ? (Loc.T("hosted.hostHint"), RowTone.Danger)
            : IsSftp ? (Loc.T("hosted.sftpHint"), RowTone.Muted)
            : (Loc.T("hosted.hostHint"), RowTone.Muted);

        (PortHint, PortTone) = IsSftp
            ? Port switch
            {
                22 => (Loc.T("hosted.portHintSftp"), RowTone.Muted),
                21 or 990 or 8821 => (Loc.T("hosted.portFtpNotSftp", Port), RowTone.Warn),
                _ => ("", RowTone.Muted),
            }
            : Port switch
            {
                21 => (Loc.T("hosted.portHintFtp"), RowTone.Muted),
                8821 => (Loc.T("hosted.portHintTcadmin"), RowTone.Muted),
                var p when IsSftpPort(p) => (Loc.T("hosted.portSftp", Port), RowTone.Warn),
                990 => (Loc.T("hosted.portImplicit"), RowTone.Muted),
                _ => ("", RowTone.Muted),
            };

        var rootHint = IsSftp ? Loc.T("hosted.rootHintSftp") : Loc.T("hosted.rootHint");
        (RootHint, RootTone) = (rootHint, NormalizedRoot is null ? RowTone.Danger : RowTone.Muted);

        // диск трогаем, только когда поле ключа на виду и в нём новый путь (или заново выбрали вход по ключу): буквы в
        // других полях не должны ждать сетевую папку, где может лежать ключ
        var keyFile = KeyFile.Trim();
        (KeyFileHint, KeyFileTone) = IsKeyLogin && keyFile.Length > 0 && !KeyFileFound(keyFile)
            ? (Loc.T("hosted.keyMissing", keyFile), RowTone.Danger)
            : (Loc.T("hosted.keyHint"), RowTone.Muted);
    }

    /// <summary>Есть ли файл ключа: ответ для этого пути запоминается; <paramref name="fresh"/> — посмотреть на диск заново.</summary>
    private bool KeyFileFound(string path, bool fresh = false)
    {
        if (fresh || _keyFileSeen is not { } seen || seen.Path != path)
        {
            seen = (path, KeyFileProbe(path));
            _keyFileSeen = seen;
        }
        return seen.Found;
    }

    /// <summary>Файл ключа есть на этом компьютере («~/» — папка пользователя, как пишут для ssh).</summary>
    private static bool KeyFileExists(string path)
    {
        try { return File.Exists(HostedEndpoint.ExpandKeyPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
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
        if (IsKeyLogin)
        {
            // перед походом на сервер — свежий взгляд на диск, и подсказка под полем ключа вместе с ним
            var found = KeyFileFound(KeyFile.Trim(), fresh: true);
            UpdateHints();
            if (!found)
            {
                // до сервера идти незачем: без файла ключа вход не получится, а «нет файла» понятнее ошибки входа
                CheckTone = RowTone.Danger;
                CheckText = Loc.T("hosted.keyMissing", KeyFile.Trim());
                return;
            }
        }
        if (EndpointSecret() is not { } secret)
        {
            // сохранённый пароль (или пароль ключа) не расшифровать — другой компьютер или пользователь: пустым его не
            // шлём — сервер ответил бы «неверный пароль», а дело не в нём
            CheckTone = RowTone.Danger;
            CheckText = IsKeyLogin ? Loc.T("hosted.keyPassphraseUnreadable") : Loc.T("hosted.passwordUnreadable");
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
            var endpoint = BuildEndpoint(secret);
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
            SetPending(ex.CertificateSha256, HostedSecret.Describe(ex), ex.IsHostKey, ex.HostKeyChanged);
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
            // без тихого понижения: сервер умеет TLS — «если сервер умеет» становится «только с шифрованием».
            // У SFTP шифрование всегда, а списка TLS нет вовсе — его не трогаем
            if (!IsSftp && Tls == FtpTls.Auto && done.Features.Encrypted)
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

    /// <summary>
    /// «Доверять этому сертификату» (или ключу сервера SFTP): отпечаток запоминается в подключении, проверка идёт заново.
    /// Ключ сервера запоминается вместе с хостом и портом: на другом порту того же хоста отвечает другой сервер.
    /// </summary>
    [RelayCommand]
    private async Task TrustCertificateAsync()
    {
        if (_pendingCertificate is not { } sha) return;
        if (_pendingIsHostKey) (_trustedHostKey, _trustedHostKeyFor) = (sha, (Host, Port ?? DefaultPort));
        else _trustedCertificate = sha;
        SetPending(null);
        await CheckAsync();
    }

    /// <summary>Показать запрос о сертификате — только для снимка окна (dialog.hostedconnect.cert).</summary>
    internal void ShowCertificatePrompt(string sha) =>
        SetPending(sha, HostedSecret.Describe(new RemoteFilesException(RemoteFailure.CertificateUntrusted, "untrusted") { CertificateSha256 = sha }));

    /// <summary>Показать запрос о ключе сервера SFTP — только для снимков окна (dialog.hostedconnect.hostkey и .hostkeychanged).</summary>
    internal void ShowHostKeyPrompt(string sha, bool changed)
    {
        var ex = new RemoteFilesException(RemoteFailure.CertificateUntrusted, "untrusted")
        {
            CertificateSha256 = sha, IsHostKey = true, HostKeyChanged = changed,
        };
        SetPending(sha, HostedSecret.Describe(ex), hostKey: true, changed);
    }

    private void SetPending(string? sha, string text = "", bool hostKey = false, bool changed = false)
    {
        _pendingCertificate = sha;
        _pendingIsHostKey = sha is not null && hostKey;
        CertificateText = sha is null ? "" : text;
        TrustText = !_pendingIsHostKey ? Loc.T("hosted.trust") : changed ? Loc.T("hosted.trustNewKey") : Loc.T("hosted.trustKey");
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

    /// <summary>«Обзор…» у файла ключа: выбранный файл встаёт в поле.</summary>
    [RelayCommand]
    private void BrowseKey()
    {
        if (PickKeyFile() is { Length: > 0 } path) KeyFile = path;
    }

    // ===== ответ =====

    /// <summary>
    /// «ОК»: собрать подключение (пароли шифруются здесь), закрепить пути из удачной проверки и отдать окну.
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

    /// <summary>
    /// Подключение из полей окна. Остальное (версия игры, «не напоминать», закреплённые пути) — от прежнего. Хранится
    /// секрет только выбранного входа: по ключу — файл и пароль ключа, по паролю — пароль; секрет прежнего способа уходит.
    /// </summary>
    private HostedConnection BuildConnection()
    {
        var c = _existing?.Clone() ?? new HostedConnection();
        var root = NormalizedRoot ?? "/";
        // другой сервер, протокол или папка: закреплённые пути старого уже не про него, раскладка найдётся заново
        if (_existing is { } e && (!string.Equals(e.Host, Host, StringComparison.OrdinalIgnoreCase) || NormalizeRoot(e.Root) != root
                                   || e.Protocol != _protocol))
            (c.ConfigPath, c.ModDirs, c.InstallDir, c.GameDir) = (null, null, null, null);
        c.Protocol = _protocol;
        c.Host = Host;
        c.Port = Port ?? DefaultPort;
        c.Tls = Tls;
        c.User = User.Trim();
        c.ActiveMode = !IsSftp && ActiveMode;
        c.Root = root;
        c.CertificateSha256 = _trustedCertificate;
        c.HostKeySha256 = IsSftp ? TrustedHostKey(c.Port) : null;
        if (IsKeyLogin)
        {
            c.KeyFile = KeyFile.Trim();
            if (KeyPassphrase.Length > 0) c.KeyPassphrase = HostedSecret.Protect(KeyPassphrase);
            c.Password = null;
        }
        else
        {
            (c.KeyFile, c.KeyPassphrase) = (null, null);
            if (Password.Length > 0) c.Password = HostedSecret.Protect(Password);
        }
        return c;
    }

    /// <summary>Ключ сервера, которому доверились, — если он про этот хост и порт; иначе null (спросим заново).</summary>
    private string? TrustedHostKey(int port) =>
        _trustedHostKeyFor is { } at && at.Port == port && string.Equals(at.Host, Host, StringComparison.OrdinalIgnoreCase) ? _trustedHostKey : null;

    /// <summary>
    /// Секрет для проверки: у входа по ключу — пароль ключа, иначе пароль. Введённый, а при пустом поле — сохранённый
    /// (расшифрованный) или пустой, если его нет. null — сохранённый есть, но его не расшифровать.
    /// </summary>
    private string? EndpointSecret()
    {
        var (typed, saved) = IsKeyLogin ? (KeyPassphrase, _existing?.KeyPassphrase) : (Password, _existing?.Password);
        return typed.Length > 0 ? typed
            : saved is { Length: > 0 } ? HostedSecret.Unprotect(saved)
            : "";
    }

    /// <summary>Подключение с расшифрованным паролем (или паролем ключа) — для одной проверки.</summary>
    private HostedEndpoint BuildEndpoint(string secret)
    {
        var port = Port ?? DefaultPort;
        var key = IsKeyLogin;
        return new HostedEndpoint(_protocol, Host, port, Tls, User.Trim(), key ? "" : secret, !IsSftp && ActiveMode, _trustedCertificate)
        {
            TrustedHostKey = IsSftp ? TrustedHostKey(port) : null,
            KeyFile = key ? HostedEndpoint.ExpandKeyPath(KeyFile) : null,
            KeyPassphrase = key && secret.Length > 0 ? secret : null,
        };
    }

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

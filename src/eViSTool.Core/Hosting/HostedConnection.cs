using eViSTool.Core.Settings;
using Newtonsoft.Json;

namespace eViSTool.Core.Hosting;

/// <summary>Протокол доступа к файлам сервера у хостера: FTP/FTPS или SFTP (файлы по SSH).</summary>
public enum HostedProtocol { Ftp, Sftp }

/// <summary>Шифрование FTP: нет, «если сервер умеет», только явный TLS (AUTH TLS), неявный TLS (обычно порт 990).</summary>
public enum FtpTls { None, Auto, Explicit, Implicit }

/// <summary>
/// Подключение к серверу у хостера (G-Portal и другие) — хранится в профиле (<see cref="Profiles.GameProfile.Hosted"/>).
/// Пароль и пароль ключа SFTP лежат зашифрованными (<see cref="HostedSecret"/>). Пути — относительно <see cref="Root"/>,
/// чтобы при смене корня не править каждое поле; null — найти автоматически при чтении.
/// Перечисления читаются терпимым конвертером: незнакомое значение не сбрасывает все настройки.
/// </summary>
public sealed class HostedConnection
{
    [JsonConverter(typeof(TolerantEnumConverter))]
    public HostedProtocol Protocol { get; set; } = HostedProtocol.Ftp;

    public string Host { get; set; } = "";

    public int Port { get; set; } = 21;

    [JsonConverter(typeof(TolerantEnumConverter))]
    public FtpTls Tls { get; set; } = FtpTls.Auto;

    public string User { get; set; } = "";

    /// <summary>Пароль, зашифрованный <see cref="HostedSecret.Protect"/>; null — пароль не сохранён (пустой).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Password { get; set; }

    /// <summary>Активный режим FTP (PORT). По умолчанию пассивный.</summary>
    public bool ActiveMode { get; set; }

    /// <summary>Корень сервера на FTP: обычно «/», у некоторых хостеров — подпапка вроде «/g123456».</summary>
    public string Root { get; set; } = "/";

    /// <summary>serverconfig.json относительно <see cref="Root"/>.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? ConfigPath { get; set; }

    /// <summary>Папки модов относительно <see cref="Root"/>.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? ModDirs { get; set; }

    /// <summary>Куда ставить новые моды, относительно <see cref="Root"/>.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? InstallDir { get; set; }

    /// <summary>Папка игры (там VintagestoryServer.dll) относительно <see cref="Root"/>.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? GameDir { get; set; }

    /// <summary>Версия игры, указанная вручную; null — определять по файлам сервера.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? GameVersion { get; set; }

    /// <summary>
    /// Версия игры, найденная последней проверкой подключения или чтением модов. Её видно сразу после запуска окна,
    /// пока моды ещё не читали; сама по себе ничего не решает — решает свежая с сервера.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? FoundGameVersion { get; set; }

    /// <summary>SHA-256 сертификата FTPS, которому человек доверился (у хостеров сертификат часто свой).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? CertificateSha256 { get; set; }

    /// <summary>
    /// SFTP: отпечаток ключа сервера SSH, которому человек доверился, — «SHA256:…», как его печатает ssh. Отдельно от
    /// <see cref="CertificateSha256"/>: у сертификата TLS отпечаток другой природы, и при смене протокола один не должен
    /// сойти за другой.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? HostKeySha256 { get; set; }

    /// <summary>SFTP: файл закрытого ключа на этом компьютере; null — вход по паролю.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? KeyFile { get; set; }

    /// <summary>SFTP: пароль ключа, зашифрованный <see cref="HostedSecret.Protect"/>, как и <see cref="Password"/>; null — ключ без пароля.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? KeyPassphrase { get; set; }

    /// <summary>Не спрашивать «останови сервер в панели хостера» перед изменением модов.</summary>
    public bool SkipStopReminder { get; set; }

    /// <summary>Независимая копия: правка профиля посреди операции её не сдвинет.</summary>
    public HostedConnection Clone()
    {
        var copy = (HostedConnection)MemberwiseClone();
        copy.ModDirs = ModDirs?.ToList();
        return copy;
    }

    /// <summary>
    /// Тот же сервер и та же учётная запись: протокол, адрес (без учёта регистра), порт, логин и корень. Пароль,
    /// шифрование и папки внутри корня не в счёт: после их правки кэш и журнал установки профиля остаются годными.
    /// Корень, который не разобрать, считается другим сервером: лучше лишний раз забыть, чем довести чужую установку.
    /// </summary>
    public bool IsSameServer(HostedConnection? other)
    {
        if (other is null || other.Protocol != Protocol || other.Port != Port || other.User != User
            || !string.Equals((other.Host ?? "").Trim(), (Host ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            return RemotePath.Normalize(other.Root ?? "/") == RemotePath.Normalize(Root ?? "/");
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Куда ведёт подключение — «ftp://логин@host:21» (с папкой, если корень не «/»). Пароля здесь нет.</summary>
    public string Describe()
    {
        var scheme = Protocol == HostedProtocol.Sftp ? "sftp" : Tls == FtpTls.Implicit ? "ftps" : "ftp";
        var user = string.IsNullOrEmpty(User) ? "" : User + "@";
        var root = string.IsNullOrEmpty(Root) || Root == "/" ? "" : (Root.StartsWith('/') ? Root : "/" + Root);
        return $"{scheme}://{user}{Host}:{Port}{root}";
    }

    /// <summary>Без пароля — на случай, если подключение попадёт в журнал или строку ошибки.</summary>
    public override string ToString() => Describe();
}

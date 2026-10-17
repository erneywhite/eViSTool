using eViSTool.Core.Localization;
using eViSTool.Core.Platform;

namespace eViSTool.Core.Hosting;

/// <summary>
/// Пароль FTP/SFTP и пароль ключа SFTP хранятся в настройках зашифрованными для текущего пользователя
/// (<see cref="SecretProtector"/>: DPAPI на Windows, AES-GCM на Linux) — как код подключения к агенту, но со своим
/// назначением: строку одного не расшифровать как другое.
/// </summary>
public static class HostedSecret
{
    private static readonly byte[] Entropy = "eViSTool hosted server password"u8.ToArray();

    public static string Protect(string password) => SecretProtector.Protect(password, Entropy);

    /// <summary>null — не расшифровать (другой пользователь, компьютер, назначение) или пусто.</summary>
    public static string? Unprotect(string? stored) => SecretProtector.Unprotect(stored, Entropy);

    /// <summary>Ошибка работы с сервером у хостера — человеческими словами. Остальные исключения — их текстом.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        RemoteFilesException { Kind: RemoteFailure.Auth } => Loc.T("hosted.errAuth"),
        RemoteFilesException { Kind: RemoteFailure.Connect } => Loc.T("hosted.errConnect", ex.Message),
        RemoteFilesException { Kind: RemoteFailure.Timeout } => Loc.T("hosted.errTimeout"),
        RemoteFilesException { Kind: RemoteFailure.Tls } => Loc.T("hosted.errTls", ex.Message),
        RemoteFilesException { Kind: RemoteFailure.CertificateUntrusted, IsHostKey: true, HostKeyChanged: true } e =>
            Loc.T("hosted.hostKeyChanged", e.CertificateSha256 ?? "?"),
        RemoteFilesException { Kind: RemoteFailure.CertificateUntrusted, IsHostKey: true } e =>
            Loc.T("hosted.trustHostKey", e.CertificateSha256 ?? "?"),
        RemoteFilesException { Kind: RemoteFailure.CertificateUntrusted } e => Loc.T("hosted.trustCert", e.CertificateSha256 ?? "?"),
        RemoteFilesException { Kind: RemoteFailure.KeyFile } => Loc.T("hosted.errKeyFile", ex.Message),
        RemoteFilesException { Kind: RemoteFailure.KeyPassphrase } => Loc.T("hosted.errKeyPassphrase"),
        RemoteFilesException { Kind: RemoteFailure.NotFound } => Loc.T("hosted.errNotFound", ex.Message),
        RemoteFilesException { Kind: RemoteFailure.Denied or RemoteFailure.Exists } => Loc.T("hosted.errDenied", ex.Message),
        RemoteFilesException { Kind: RemoteFailure.Quota } => Loc.T("hosted.errQuota"),
        RemoteFilesException { Kind: RemoteFailure.Unsupported } => Loc.T("hosted.errNoTransport"),
        RemoteFilesException => Loc.T("hosted.errProtocol", ex.Message),
        TimeoutException => Loc.T("hosted.errTimeout"),
        _ => ex.Message,
    };
}

namespace eViSTool.Core.Hosting;

/// <summary>
/// Готовое к подключению описание сервера: то же, что <see cref="HostedConnection"/>, но с расшифрованными паролем и
/// паролем ключа. Живёт только в памяти, в настройки не пишется. <see cref="ToString"/> секретов не печатает: запись
/// иначе выдала бы их в журнал при первой же интерполяции.
/// </summary>
public sealed record HostedEndpoint(HostedProtocol Protocol, string Host, int Port, FtpTls Tls, string User, string Password,
    bool ActiveMode, string? TrustedCertificateSha256)
{
    /// <summary>Тайм-аут одной команды или передачи.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>SFTP: отпечаток ключа сервера, которому доверяют («SHA256:…»); null — не доверяют никакому.</summary>
    public string? TrustedHostKey { get; init; }

    /// <summary>SFTP: файл закрытого ключа на этом компьютере; null — вход по паролю.</summary>
    public string? KeyFile { get; init; }

    /// <summary>SFTP: пароль ключа открытым текстом; null — ключ без пароля.</summary>
    public string? KeyPassphrase { get; init; }

    /// <summary>Из настроек профиля. Пароль не сохранён — пустой; сохранён, но не расшифровывается на этом
    /// компьютере (другой пользователь, перенесли папку данных) — null. Так же и с паролем ключа SFTP.</summary>
    public static HostedEndpoint? From(HostedConnection connection)
    {
        var password = "";
        if (connection.Password is { } stored)
        {
            if (HostedSecret.Unprotect(stored) is not { } plain) return null;
            password = plain;
        }
        string? passphrase = null;
        if (connection.KeyPassphrase is { } storedKey)
        {
            if (HostedSecret.Unprotect(storedKey) is not { } plainKey) return null;
            passphrase = plainKey;
        }
        return new HostedEndpoint(connection.Protocol, connection.Host.Trim(), connection.Port, connection.Tls, connection.User,
            password, connection.ActiveMode, connection.CertificateSha256)
        {
            TrustedHostKey = connection.HostKeySha256,
            KeyFile = string.IsNullOrWhiteSpace(connection.KeyFile) ? null : ExpandKeyPath(connection.KeyFile),
            KeyPassphrase = passphrase,
        };
    }

    /// <summary>Путь к файлу ключа: «~/.ssh/id_ed25519» — от папки пользователя, как его пишут по привычке из ssh.</summary>
    public static string ExpandKeyPath(string path)
    {
        var p = path.Trim();
        if (p == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return p.StartsWith("~/", StringComparison.Ordinal) || p.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[2..])
            : p;
    }

    /// <summary>Без пароля и пароля ключа.</summary>
    public override string ToString()
    {
        var user = string.IsNullOrEmpty(User) ? "" : User + "@";
        if (Protocol == HostedProtocol.Sftp) return $"sftp://{user}{Host}:{Port} ({(KeyFile is null ? "password" : "key")})";
        return $"{Protocol.ToString().ToLowerInvariant()}://{user}{Host}:{Port} (tls: {Tls}{(ActiveMode ? ", active" : "")})";
    }
}

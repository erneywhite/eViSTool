namespace eViSTool.Core.Hosting;

/// <summary>
/// Готовое к подключению описание сервера: то же, что <see cref="HostedConnection"/>, но с расшифрованным паролем.
/// Живёт только в памяти, в настройки не пишется. <see cref="ToString"/> пароль не печатает: запись иначе выдала бы
/// его в журнал при первой же интерполяции.
/// </summary>
public sealed record HostedEndpoint(HostedProtocol Protocol, string Host, int Port, FtpTls Tls, string User, string Password,
    bool ActiveMode, string? TrustedCertificateSha256)
{
    /// <summary>Тайм-аут одной команды или передачи.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Из настроек профиля. Пароль не сохранён — пустой; сохранён, но не расшифровывается на этом
    /// компьютере (другой пользователь, перенесли папку данных) — null.</summary>
    public static HostedEndpoint? From(HostedConnection connection)
    {
        var password = "";
        if (connection.Password is { } stored)
        {
            if (HostedSecret.Unprotect(stored) is not { } plain) return null;
            password = plain;
        }
        return new HostedEndpoint(connection.Protocol, connection.Host.Trim(), connection.Port, connection.Tls, connection.User,
            password, connection.ActiveMode, connection.CertificateSha256);
    }

    /// <summary>Без пароля.</summary>
    public override string ToString()
    {
        var user = string.IsNullOrEmpty(User) ? "" : User + "@";
        return $"{Protocol.ToString().ToLowerInvariant()}://{user}{Host}:{Port} (tls: {Tls}{(ActiveMode ? ", active" : "")})";
    }
}

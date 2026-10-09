using eViSTool.Core.Localization;
using eViSTool.Core.Platform;

namespace eViSTool.Core.Server.Remote;

/// <summary>
/// Код подключения к удалённому серверу хранится в настройках зашифрованным для текущего пользователя
/// (<see cref="SecretProtector"/>: DPAPI на Windows): скопированная или утёкшая папка data на другом компьютере его не
/// раскроет. Цена — на новом компьютере код придётся вставить заново.
/// </summary>
public static class RemoteSecret
{
    private static readonly byte[] Entropy = "eViSTool remote connection code"u8.ToArray();

    public static string Protect(ConnectionCode code) => SecretProtector.Protect(code.ToString(), Entropy);

    /// <summary>null — не расшифровать (другой пользователь или компьютер) или внутри не код.</summary>
    public static ConnectionCode? Unprotect(string? stored) =>
        SecretProtector.Unprotect(stored, Entropy) is { } text && ConnectionCode.TryParse(text, out var code) ? code : null;

    /// <summary>Ошибка подключения к удалённому агенту — человеческими словами.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        InvalidOperationException { Message: var m } when m.StartsWith("401") => Loc.T("remote.errKey"),
        InvalidOperationException { Message: var m } when m.StartsWith("429") => Loc.T("remote.errTooMany"),
        InvalidOperationException { Message: var m } when m.StartsWith("404") => Loc.T("remote.errOldAgent"),
        HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } => Loc.T("remote.errCertificate"),
        HttpRequestException or TaskCanceledException => Loc.T("remote.errNoConnection", ex.Message),
        _ => ex.Message,
    };
}

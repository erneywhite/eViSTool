using System.Security.Cryptography;
using System.Text;
using eViSTool.Core.Localization;

namespace eViSTool.Core.Server.Remote;

/// <summary>
/// Код подключения к удалённому серверу хранится в настройках зашифрованным средствами Windows (DPAPI, для текущего
/// пользователя): скопированная или утёкшая папка data на другом компьютере его не раскроет. Цена — на новом компьютере
/// код придётся вставить заново.
/// </summary>
public static class RemoteSecret
{
    private static readonly byte[] Entropy = "eViSTool remote connection code"u8.ToArray();

    public static string Protect(ConnectionCode code) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(code.ToString()), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>null — не расшифровать (другой пользователь или компьютер) или внутри не код.</summary>
    public static ConnectionCode? Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        try
        {
            var text = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser));
            return ConnectionCode.TryParse(text, out var code) ? code : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

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

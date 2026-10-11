using System.Security.Cryptography;

namespace eViSTool.Core.AppUpdate;

/// <summary>
/// Подпись архивов обновления. Контрольная сумма лежит в том же релизе, что и архив, и защищает только от порчи: кто
/// подменит архив на GitHub, подменит и сумму. Подпись делается закрытым ключом, который есть только у сборки релиза
/// в CI (секрет GitHub), а открытый ключ зашит в программу — архив без верной подписи окно и агент не ставят.
/// Подпись — ECDSA P-256 над SHA-256 файла, в «&lt;архив&gt;.sig» рядом с архивом, base64.
/// </summary>
public static class UpdateSignature
{
    public const string Extension = ".sig";

    /// <summary>Открытый ключ релизов eViSTool (SubjectPublicKeyInfo, base64).</summary>
    public const string ReleaseKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAENsiFMxsuYCFEpz5AtJz4kvpg8Aud+bj3IjjwJtu6IakPhyBw+W59tkUR2EmGuixGmCrtJsfRaW4fIowuYPoidA==";

    /// <summary>
    /// Переменная окружения с другим открытым ключом — для стендов и тестов с поддельной лентой релизов (вместе с
    /// EVISTOOL_RELEASES_URL). Задать её может только тот, кто и так управляет этой машиной.
    /// </summary>
    public const string KeyVariable = "EVISTOOL_UPDATE_PUBLIC_KEY";

    /// <summary>Каким ключом проверять: заданный в окружении или ключ релизов.</summary>
    public static string CurrentKey =>
        Environment.GetEnvironmentVariable(KeyVariable) is { Length: > 0 } key ? key : ReleaseKey;

    /// <summary>Подпись файла верна для этого ключа. Битая подпись или ключ — false, не исключение.</summary>
    public static bool Verify(string file, string signature, string publicKey)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            using var stream = File.OpenRead(file);
            return ecdsa.VerifyData(stream, Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Подписать файл закрытым ключом (PKCS#8, base64) — так же, как сборка релиза в CI; нужно тестам.</summary>
    public static string Sign(string file, string privateKey)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);
        using var stream = File.OpenRead(file);
        return Convert.ToBase64String(ecdsa.SignData(stream, HashAlgorithmName.SHA256));
    }

    /// <summary>Новая пара ключей: (закрытый PKCS#8, открытый SubjectPublicKeyInfo), оба base64.</summary>
    public static (string PrivateKey, string PublicKey) NewKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()), Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
    }
}

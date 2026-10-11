using System.Security.Cryptography;
using eViSTool.Core.AppUpdate;

namespace eViSTool.Core.Tests;

/// <summary>Тестовая пара ключей подписи обновлений: поддельные релизы подписываются ей, программа проверяет её открытым ключом.</summary>
internal static class TestSigning
{
    private static readonly (string Private, string Public) Keys = UpdateSignature.NewKeyPair();

    public static string PublicKey => Keys.Public;

    /// <summary>Подпись содержимого — как «.sig» рядом с архивом релиза.</summary>
    public static string Sign(byte[] data)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(Keys.Private), out _);
        return Convert.ToBase64String(ecdsa.SignData(data, HashAlgorithmName.SHA256));
    }
}

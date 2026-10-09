using System.Security.Cryptography;
using System.Text;
using eViSTool.Core.Notifications;
using eViSTool.Core.Platform;

namespace eViSTool.Core.Tests;

/// <summary>Секреты без DPAPI (Linux): AES-256-GCM, ключ в файле пользователя с правами 600.</summary>
public sealed class SecretKeyFileTests : IDisposable
{
    private static readonly byte[] Purpose = "test purpose"u8.ToArray();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evistool-key-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void KeyIsCreatedOnFirstUse_OnlyForTheOwner_AndReadByOthersWithTheSamePlace()
    {
        var file = Path.Combine(_dir, "user", "eViSTool", SecretKeyFile.FileName);
        var keys = new SecretKeyFile(file);
        // расшифровка ключ не заводит: новым ключом старую строку всё равно не открыть
        Assert.Null(keys.Unprotect(SecretKeyFile.Prefix + Convert.ToBase64String(new byte[40]), Purpose));
        Assert.False(File.Exists(file));

        var stored = keys.Protect("123456789:AAHdqTcv", Purpose);
        Assert.StartsWith(SecretKeyFile.Prefix, stored);
        Assert.DoesNotContain("AAHdqTcv", stored);
        Assert.NotEqual(stored, keys.Protect("123456789:AAHdqTcv", Purpose)); // каждый раз своё случайное начало
        Assert.Equal("123456789:AAHdqTcv", keys.Unprotect(stored, Purpose));

        Assert.Equal(file, keys.KeyPath);
        Assert.Equal(32, new FileInfo(file).Length);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!)); // временных файлов не осталось

        // другой процесс с тем же местом — тот же ключ
        Assert.Equal("123456789:AAHdqTcv", new SecretKeyFile(file).Unprotect(stored, Purpose));
    }

    [Fact]
    public void OpenedKey_IsClosedBackTo600()
    {
        if (OperatingSystem.IsWindows()) return;
        var file = Path.Combine(_dir, SecretKeyFile.FileName);
        var stored = new SecretKeyFile(file).Protect("s", Purpose);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        Assert.Equal("s", new SecretKeyFile(file).Unprotect(stored, Purpose));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [Fact]
    public void ForeignOrDamagedStrings_AreNotDecrypted()
    {
        var keys = new SecretKeyFile(Path.Combine(_dir, "a", SecretKeyFile.FileName));
        var stored = keys.Protect("secret", Purpose);

        Assert.Null(keys.Unprotect(stored, "other purpose"u8)); // зашифровано для другого
        var other = new SecretKeyFile(Path.Combine(_dir, "b", SecretKeyFile.FileName));
        other.Protect("x", Purpose);
        Assert.Null(other.Unprotect(stored, Purpose)); // чужой ключ

        var blob = Convert.FromBase64String(stored[SecretKeyFile.Prefix.Length..]);
        blob[^1] ^= 1;
        Assert.Null(keys.Unprotect(SecretKeyFile.Prefix + Convert.ToBase64String(blob), Purpose)); // подменили байт
        Assert.Null(keys.Unprotect(Convert.ToBase64String(new byte[64]), Purpose)); // DPAPI с Windows — без приставки
        Assert.Null(keys.Unprotect(SecretKeyFile.Prefix + "не base64", Purpose));
        Assert.Null(keys.Unprotect(SecretKeyFile.Prefix + "AAAA", Purpose));
        Assert.Null(keys.Unprotect(null, Purpose));
    }

    [Fact]
    public void UnwritableHome_KeyGoesToTheSparePlace_AndIsNeverDoubled()
    {
        // «домашняя папка» — файл: в ней ни папку, ни ключ не создать
        Directory.CreateDirectory(_dir);
        var blocked = Path.Combine(_dir, "home");
        File.WriteAllText(blocked, "");
        var home = Path.Combine(blocked, "eViSTool", SecretKeyFile.FileName);
        var spare = Path.Combine(_dir, "data", SecretKeyFile.FileName);

        var stored = new SecretKeyFile(home, spare).Protect("s", Purpose);
        Assert.True(File.Exists(spare));

        // домашняя папка появилась — ключ всё равно из запасного места, второй не заводится
        File.Delete(blocked);
        var keys = new SecretKeyFile(home, spare);
        Assert.Equal("s", keys.Unprotect(stored, Purpose));
        keys.Protect("t", Purpose);
        Assert.False(File.Exists(home));
        Assert.Equal(spare, keys.KeyPath);
    }

    [Fact]
    public void ManyAtOnce_AgreeOnOneKey()
    {
        var file = Path.Combine(_dir, "race", SecretKeyFile.FileName);
        var stored = new string[16];
        Parallel.For(0, stored.Length, i => stored[i] = new SecretKeyFile(file).Protect("s" + i, Purpose));

        var keys = new SecretKeyFile(file);
        for (var i = 0; i < stored.Length; i++) Assert.Equal("s" + i, keys.Unprotect(stored[i], Purpose));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!));
    }

    [Fact]
    public void NotifySecret_OnLinuxWithTheKey_OnWindowsDpapiAsBefore()
    {
        var stored = NotifySecret.Protect("tok");
        Assert.Equal(!OperatingSystem.IsWindows(), stored.StartsWith(SecretKeyFile.Prefix, StringComparison.Ordinal));
        Assert.Equal("tok", NotifySecret.Unprotect(stored));
        Assert.Null(SecretProtector.Unprotect(stored, "eViSTool remote connection code"u8.ToArray())); // для другого назначения — нет

        if (!OperatingSystem.IsWindows()) return;
        // строка, сохранённая прежней версией (DPAPI), читается как раньше
        var old = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes("tok"), "eViSTool notification secret"u8.ToArray(),
            DataProtectionScope.CurrentUser));
        Assert.Equal("tok", NotifySecret.Unprotect(old));
    }
}

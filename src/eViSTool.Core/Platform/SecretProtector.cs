using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace eViSTool.Core.Platform;

/// <summary>
/// Секреты (токены оповещений, код подключения) шифруются для текущего пользователя этого компьютера: скопированная
/// или утёкшая папка данных их не раскроет. На Windows — DPAPI, как было всегда: у людей уже лежат такие строки.
/// На Linux DPAPI нет — там AES-256-GCM с ключом в файле пользователя (<see cref="SecretKeyFile"/>), и строка
/// начинается с <see cref="SecretKeyFile.Prefix"/>, чтобы её не спутать с DPAPI.
/// </summary>
public static class SecretProtector
{
    /// <param name="purpose">Для чего секрет: строку, зашифрованную для одного, не расшифровать как другое.</param>
    public static string Protect(string secret, byte[] purpose)
    {
        if (!OperatingSystem.IsWindows()) return SecretKeyFile.ForCurrentUser.Protect(secret, purpose);
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), purpose, DataProtectionScope.CurrentUser));
    }

    /// <summary>null — не расшифровать (другой пользователь, компьютер или ключ) или пусто.</summary>
    public static string? Unprotect(string? stored, byte[] purpose)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        if (!OperatingSystem.IsWindows()) return SecretKeyFile.ForCurrentUser.Unprotect(stored, purpose);
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), purpose, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// Шифрование без DPAPI: AES-256-GCM, ключ — 32 случайных байта в файле с правами 600 (читает только владелец).
/// Места для ключа перебираются по порядку: где он уже есть, тот и берётся; нет нигде — создаётся в первом, куда
/// можно писать. Так ключ не раздваивается, даже если домашняя папка появилась после того, как ключ лёг в запасное место.
/// </summary>
public sealed class SecretKeyFile
{
    /// <summary>Начало зашифрованной строки, дальше base64 от nonce, метки и шифротекста. В base64 двоеточия не
    /// бывает, так что строку DPAPI с этой не спутать.</summary>
    public const string Prefix = "aes1:";

    public const string FileName = "secret.key";

    private const int KeySize = 32, NonceSize = 12, TagSize = 16;

    private static readonly Lazy<SecretKeyFile> _currentUser = new(() => new SecretKeyFile(DefaultPlaces()));

    private readonly string[] _places;

    public SecretKeyFile(params string[] places)
    {
        if (places.Length == 0) throw new ArgumentException("no place for the key", nameof(places));
        _places = places;
    }

    /// <summary>
    /// Ключ текущего пользователя — в его папке данных (~/.local/share/eViSTool), как DPAPI у пользователя Windows: агент
    /// и всё, что запущено от того же пользователя, читают один ключ, а копия папки программы без него секретов не раскроет.
    /// Домашней папки нет или туда нельзя писать (служебный пользователь) — ключ в папке данных программы.
    /// </summary>
    public static SecretKeyFile ForCurrentUser => _currentUser.Value;

    private static string[] DefaultPlaces()
    {
        var places = new List<string>();
        if (XdgDirs.DataHome() is { } home) places.Add(Path.Combine(home, "eViSTool", FileName));
        var data = Path.Combine(AppPaths.Root, FileName);
        if (!places.Contains(data)) places.Add(data);
        return [.. places];
    }

    /// <summary>Где лежит ключ сейчас; null — ещё нигде (создастся при первом шифровании).</summary>
    public string? KeyPath => _places.FirstOrDefault(p => Read(p) is not null);

    public string Protect(string secret, ReadOnlySpan<byte> purpose)
    {
        var key = LoadOrCreate();
        var plain = Encoding.UTF8.GetBytes(secret);
        var blob = new byte[NonceSize + TagSize + plain.Length];
        var nonce = blob.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), purpose);
        return Prefix + Convert.ToBase64String(blob);
    }

    /// <summary>null — не расшифровать: не наша строка (например, DPAPI с Windows), другой ключ, другое назначение или ключа нет.</summary>
    public string? Unprotect(string? stored, ReadOnlySpan<byte> purpose)
    {
        if (stored is null || !stored.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            var blob = Convert.FromBase64String(stored[Prefix.Length..]);
            // ради расшифровки ключ не создаём: новым ключом старую строку всё равно не открыть
            if (blob.Length < NonceSize + TagSize || Load() is not { } key) return null;
            var plain = new byte[blob.Length - NonceSize - TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), plain, purpose);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private byte[]? Load()
    {
        foreach (var place in _places)
            if (Read(place) is { } key) return key;
        return null;
    }

    private byte[] LoadOrCreate()
    {
        if (Load() is { } key) return key;
        Exception? failure = null;
        foreach (var place in _places)
        {
            try
            {
                if (Create(place) is { } made) return made;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex; // сюда писать нельзя — пробуем следующее место
            }
        }
        throw failure ?? new IOException($"{_places[0]}: damaged key file");
    }

    /// <summary>Ключ из файла; null — файла нет или он повреждён. Чужой файл, который нельзя прочитать, — исключение:
    /// заводить рядом второй ключ нельзя.</summary>
    private static byte[]? Read(string file)
    {
        byte[] key;
        try
        {
            key = File.ReadAllBytes(file);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        if (key.Length != KeySize) return null;
        if (!OperatingSystem.IsWindows()) Tighten(file);
        return key;
    }

    /// <summary>
    /// Новый ключ: сначала во временный файл, сразу с правами 600, потом переименованием на место. Соседний процесс не
    /// прочитает полузаписанный ключ, а два процесса разом не создадут два разных: второй возьмёт ключ первого.
    /// null — на месте уже лежит повреждённый файл.
    /// </summary>
    private static byte[]? Create(string file)
    {
        XdgDirs.CreatePrivate(Path.GetDirectoryName(file)!);
        var key = RandomNumberGenerator.GetBytes(KeySize);
        var tmp = $"{file}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var stream = new FileStream(tmp, options)) stream.Write(key);
            try
            {
                File.Move(tmp, file, overwrite: false);
                return key;
            }
            catch (IOException) when (File.Exists(file))
            {
                return Read(file);
            }
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>Ключ, открытый группе или всем (скопировали без сохранения прав), закрываем обратно до 600.</summary>
    [UnsupportedOSPlatform("windows")]
    private static void Tighten(string file)
    {
        const UnixFileMode open = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        try
        {
            var mode = File.GetUnixFileMode(file);
            if ((mode & open) != 0) File.SetUnixFileMode(file, mode & ~open);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // файл чужой: читать можем, менять права — нет
        }
    }
}

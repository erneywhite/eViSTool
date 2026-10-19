using System.Buffers.Binary;
using System.IO.Compression;

namespace eViSTool.Core.Mods;

/// <summary>
/// Иконка мода — modicon.png в корне архива или папки мода: квадратная картинка, которую автор делает для списка модов
/// в игре (docs/design.md, раздел 7). Только с диска этой машины, без сети.
/// </summary>
public static class ModIcon
{
    public const string FileName = "modicon.png";

    /// <summary>
    /// Больше — не иконка, а что-то чужое: у самых крупных встреченных иконок около мегабайта (2048 × 2048). Такое не
    /// читаем, чтобы не держать в памяти мусор.
    /// </summary>
    public const long MaxBytes = 8L << 20;

    /// <summary>
    /// Байты modicon.png из мода: zip — запись в корне архива, папка — файл в ней; имя без учёта регистра (точное — в
    /// первую очередь). Иконки нет, мод не zip и не папка (.dll, .cs), архив битый или не читается — null.
    /// </summary>
    public static byte[]? Read(string modPath)
    {
        try
        {
            if (Directory.Exists(modPath)) return FromFolder(modPath);
            if (!Path.GetExtension(modPath).Equals(".zip", StringComparison.OrdinalIgnoreCase) || !File.Exists(modPath)) return null;
            using var zip = ZipFile.OpenRead(modPath);
            var entry = zip.GetEntry(FileName)
                        ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals(FileName, StringComparison.OrdinalIgnoreCase));
            if (entry is null || entry.Length > MaxBytes) return null;
            using var stream = entry.Open();
            return ReadAll(stream);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException)
        {
            // архив битый, файл заняли или удалили, пока читали — иконки просто нет
            return null;
        }
    }

    private static byte[]? FromFolder(string dir)
    {
        var exact = new FileInfo(Path.Combine(dir, FileName));
        var file = exact.Exists ? exact
            : new DirectoryInfo(dir).EnumerateFiles().FirstOrDefault(f => f.Name.Equals(FileName, StringComparison.OrdinalIgnoreCase));
        if (file is null || file.Length > MaxBytes) return null;
        using var stream = file.OpenRead();
        return ReadAll(stream);
    }

    /// <summary>
    /// Прочитать до конца, но не больше <see cref="MaxBytes"/>: размер в заголовке архива может врать, а распаковка
    /// честно отдаёт столько, сколько там на самом деле.
    /// </summary>
    private static byte[]? ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + n > MaxBytes) return null;
            buffer.Write(chunk, 0, n);
        }
        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    /// <summary>
    /// Размер картинки из заголовка PNG (IHDR) без декодирования; не PNG или заголовок битый — null. Нужен, чтобы
    /// уменьшать неквадратную иконку по короткой стороне: после обрезки до квадрата она не станет мыльной.
    /// </summary>
    public static (int Width, int Height)? PngSize(ReadOnlySpan<byte> png)
    {
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length < 24 || !png[..8].SequenceEqual(signature) || !png.Slice(12, 4).SequenceEqual("IHDR"u8)) return null;
        var width = BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4));
        return width > 0 && height > 0 ? (width, height) : null;
    }
}

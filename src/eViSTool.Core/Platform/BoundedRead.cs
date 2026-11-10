using System.IO.Compression;
using System.Text;
using eViSTool.Core.Localization;

namespace eViSTool.Core.Platform;

/// <summary>
/// Чтение из архивов с пределом. Размер в заголовке zip может врать, а распаковка честно отдаёт столько, сколько там на
/// самом деле: маленький архив-«бомба» с modinfo.json на гигабайты съел бы всю память при обычном ReadToEnd.
/// </summary>
public static class BoundedRead
{
    /// <summary>Предел для текстовых файлов внутри мода и пака: modinfo.json, манифест, настройки, переводы.</summary>
    public const long TextLimit = 8L << 20;

    /// <summary>Предел для файла мода внутри пака — как у установки мода через агента.</summary>
    public const long ModFileLimit = 1L << 30;

    /// <summary>Текст записи архива; больше <paramref name="max"/> — <see cref="InvalidDataException"/>.</summary>
    public static string Text(ZipArchiveEntry entry, long max = TextLimit)
    {
        using var stream = entry.Open();
        return Text(stream, max, entry.FullName);
    }

    /// <summary>Текст из потока (UTF-8, метка порядка байтов учитывается); больше <paramref name="max"/> — исключение.</summary>
    public static string Text(Stream stream, long max = TextLimit, string? name = null)
    {
        using var buffer = new MemoryStream();
        CopyLimited(stream, buffer, max, name);
        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Распаковать запись в файл, но не больше <paramref name="max"/>: больше — файл удаляется, исключение.</summary>
    public static void ExtractToFile(ZipArchiveEntry entry, string dest, long max = ModFileLimit)
    {
        try
        {
            using var from = entry.Open();
            using var to = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            CopyLimited(from, to, max, entry.FullName);
        }
        catch
        {
            try { File.Delete(dest); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void CopyLimited(Stream from, Stream to, long max, string? name)
    {
        var chunk = new byte[81920];
        long total = 0;
        int n;
        while ((n = from.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += n;
            if (total > max) throw new InvalidDataException(Loc.T("zip.entryTooBig", name ?? "?", max / (1024 * 1024)));
            to.Write(chunk, 0, n);
        }
    }
}

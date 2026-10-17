using System.IO.Compression;
using System.Text;

namespace eViSTool.Core.Hosting;

/// <summary>Файл на сервере, из которого можно читать куски (по FTP — REST + RETR) или скачать целиком.</summary>
public interface IRangeSource
{
    long Length { get; }

    /// <summary>Кусок с позиции; вернулось меньше <paramref name="count"/> — конец файла.</summary>
    Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct);

    /// <summary>Весь файл потоком (запасной путь: сервер не читает с позиции или архив необычный).</summary>
    Task<Stream> OpenAllAsync(CancellationToken ct);
}

/// <summary>
/// Что лежит в архиве мода: сырой текст modinfo.json или ключ ошибки (<c>scan.noModInfo</c>, <c>scan.nestedModInfo</c>,
/// <c>scan.corrupt</c>). Текст ошибки не храним — он на языке окна; сырой JSON — чтобы кэш пережил смену правил разбора.
/// Разбирает его <see cref="Mods.ModScanner.FromCached"/>.
/// </summary>
public sealed record CachedModInfo(string? ModInfoJson, string? ErrorKey);

/// <summary>
/// modinfo.json из архива мода на сервере — по возможности без полного скачивания (алгоритм — в проекте, п. 3.2).
/// Ошибка в устройстве архива — <c>scan.corrupt</c>, наружу не выходит; сбой сети выходит как есть.
/// </summary>
public static class ZipProbe
{
    public const int WholeFileLimit = 256 * 1024, TailSize = 22 + 65_535, MaxModInfoSize = 1024 * 1024;

    private const string ModInfoFile = "modinfo.json";

    public static async Task<CachedModInfo> ReadModInfoAsync(IRangeSource source, CancellationToken ct)
    {
        // shortcut: архив всегда скачивается целиком; частичное чтение (хвост, центральный каталог, одна запись) —
        // пакет P1, переделать там же
        var stream = await source.OpenAllAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            if (!stream.CanSeek)
            {
                var copy = new MemoryStream();
                await stream.CopyToAsync(copy, ct).ConfigureAwait(false); // сбой сети — наружу, это не «битый архив»
                copy.Position = 0;
                return await Task.Run(() => ReadZip(copy), ct).ConfigureAwait(false);
            }
            return await Task.Run(() => ReadZip(stream), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Разбор архива из потока — как <see cref="Mods.ModScanner.ReadZip"/>: modinfo.json в корне без учёта регистра.</summary>
    internal static CachedModInfo ReadZip(Stream zipStream)
    {
        try
        {
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(ModInfoFile, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                var nested = zip.Entries.Any(e => e.Name.Equals(ModInfoFile, StringComparison.OrdinalIgnoreCase));
                return new CachedModInfo(null, nested ? "scan.nestedModInfo" : "scan.noModInfo");
            }
            if (entry.Length > MaxModInfoSize) return new CachedModInfo(null, "scan.corrupt"); // защита от zip-бомбы

            using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return new CachedModInfo(reader.ReadToEnd(), null);
        }
        catch (InvalidDataException)
        {
            return new CachedModInfo(null, "scan.corrupt");
        }
    }
}

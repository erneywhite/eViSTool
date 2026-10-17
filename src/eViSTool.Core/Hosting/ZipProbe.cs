using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace eViSTool.Core.Hosting;

/// <summary>
/// Файл на сервере, из которого можно читать куски (по FTP — REST + RETR) или скачать целиком.
/// <para>
/// Кусок, который кончается раньше конца файла (<c>offset + count &lt; Length</c>, см. <see cref="ZipProbe.CutsShort"/>), —
/// это «чтение с обрывом»: передачу приходится закрывать посередине. У части серверов (G-Portal) после этого умирает
/// управляющее соединение, поэтому источник сам отвечает за то, чтобы после такого чтения соединение снова было
/// годным (переподключиться сразу, а не надеяться на NOOP/ABOR). <see cref="ZipProbe"/> по возможности читает до
/// конца файла и обрывает передачу, только когда дочитывать слишком дорого (<see cref="ReadThroughLimit"/>).
/// </para>
/// </summary>
public interface IRangeSource
{
    long Length { get; }

    /// <summary>Кусок с позиции; вернулось меньше <paramref name="count"/> — конец файла.
    /// Сервер не читает с позиции — <see cref="RemoteFilesException"/> с <see cref="RemoteFailure.RangeUnsupported"/>:
    /// тогда архив скачивается целиком (<see cref="OpenAllAsync"/>).</summary>
    Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct);

    /// <summary>Весь файл потоком (запасной путь: сервер не читает с позиции или архив необычный).</summary>
    Task<Stream> OpenAllAsync(CancellationToken ct);

    /// <summary>
    /// Сколько лишних байт выгоднее дочитать до конца файла, чем оборвать передачу: у FTP обрыв стоит переподключения.
    /// 0 — обрыв ничего не стоит (файл на диске): читается ровно нужное.
    /// </summary>
    long ReadThroughLimit => ZipProbe.DefaultReadThroughLimit;
}

/// <summary>
/// Что лежит в архиве мода: сырой текст modinfo.json или ключ ошибки (<c>scan.noModInfo</c>, <c>scan.nestedModInfo</c>,
/// <c>scan.corrupt</c>). Текст ошибки не храним — он на языке окна; сырой JSON — чтобы кэш пережил смену правил разбора.
/// Разбирает его <see cref="Mods.ModScanner.FromCached"/>.
/// </summary>
public sealed record CachedModInfo(string? ModInfoJson, string? ErrorKey);

/// <summary>
/// modinfo.json из архива мода на сервере — по возможности без полного скачивания (алгоритм — в проекте, п. 3.2):
/// хвост файла с концом центрального каталога, сам каталог, затем одна запись modinfo.json. Обычно хватает одной-двух
/// передач, и почти всегда они идут до конца файла (без обрыва). Небольшой файл, Zip64, многотомный архив, необычный
/// метод сжатия и сервер без чтения с позиции — скачивание целиком.
/// Ошибка в устройстве архива — <c>scan.corrupt</c>, наружу не выходит; сбой сети выходит как есть.
/// </summary>
public static class ZipProbe
{
    public const int WholeFileLimit = 256 * 1024, TailSize = 22 + 65_535, MaxModInfoSize = 1024 * 1024;

    /// <summary>По умолчанию <see cref="IRangeSource.ReadThroughLimit"/>: полмегабайта дочитать дешевле переподключения.</summary>
    public const long DefaultReadThroughLimit = 512 * 1024;

    private const string ModInfoFile = "modinfo.json";
    private const uint EocdSig = 0x06054B50, Zip64LocatorSig = 0x07064B50, CentralSig = 0x02014B50, LocalSig = 0x04034B50;
    private const int EocdSize = 22, CentralSize = 46, LocalSize = 30;

    /// <summary>Чтение кончается раньше конца файла — передачу придётся оборвать (см. <see cref="IRangeSource"/>).</summary>
    public static bool CutsShort(this IRangeSource source, long offset, int count) => offset + count < source.Length;

    public static async Task<CachedModInfo> ReadModInfoAsync(IRangeSource source, CancellationToken ct)
    {
        if (source.Length > WholeFileLimit)
        {
            try
            {
                return await ReadByRangesAsync(new Reader(source), ct).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return Corrupt;
            }
            catch (NeedWholeFile)
            {
                // Zip64, многотомный, необычный метод сжатия или файл поменялся по ходу — ниже, целиком
            }
            catch (RemoteFilesException ex) when (ex.Kind == RemoteFailure.RangeUnsupported)
            {
                // сервер не читает с позиции — ниже, целиком
            }
        }
        return await ReadWholeAsync(source, ct).ConfigureAwait(false);
    }

    private static CachedModInfo Corrupt => new(null, "scan.corrupt");

    private static async Task<CachedModInfo> ReadWholeAsync(IRangeSource source, CancellationToken ct)
    {
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
            if (entry.Length > MaxModInfoSize) return Corrupt; // защита от zip-бомбы

            using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return new CachedModInfo(reader.ReadToEnd(), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            // NotSupportedException — шифрование или метод сжатия, которого ZipArchive не знает
            return Corrupt;
        }
    }

    // ---------- чтение кусками

    private static async Task<CachedModInfo> ReadByRangesAsync(Reader reader, CancellationToken ct)
    {
        var length = reader.Length;

        // 1. Хвост: конец центрального каталога (22 байта) плюс самый длинный комментарий. Читается до конца файла.
        var tailStart = length - Math.Min(length, TailSize);
        var tail = await reader.GetAsync(tailStart, length, ct).ConfigureAwait(false);
        var eocd = FindEocd(tail);
        if (eocd < 0) throw new InvalidDataException("no end of central directory");
        var eocdAt = tailStart + eocd;
        if (eocd >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd - 20)) == Zip64LocatorSig)
            throw new NeedWholeFile();

        var e = tail.AsSpan(eocd);
        var disk = U16(e, 4);
        var cdDisk = U16(e, 6);
        var entriesHere = U16(e, 8);
        var entries = U16(e, 10);
        var cdSize = U32(e, 12);
        var cdOffset = U32(e, 16);
        if (disk == 0xFFFF || cdDisk == 0xFFFF || entriesHere == 0xFFFF || entries == 0xFFFF
            || cdSize == uint.MaxValue || cdOffset == uint.MaxValue)
            throw new NeedWholeFile(); // Zip64: модов таких размеров не бывает, разбирать незачем
        if (disk != 0 || cdDisk != 0 || entriesHere != entries) throw new NeedWholeFile(); // многотомный

        // Сдвиг для архивов с данными в начале (самораспаковывающиеся): смещения в каталоге — от начала архива.
        var cdStart = eocdAt - cdSize;
        var shift = cdStart - cdOffset;
        if (cdStart < 0 || shift < 0) throw new InvalidDataException("central directory outside the file");

        // 2. Центральный каталог: если не уместился в хвост — второй кусок, тоже до конца файла.
        var cd = await reader.GetAsync(cdStart, eocdAt, ct).ConfigureAwait(false);
        var found = FindModInfo(cd, entries);
        if (found.ErrorKey is { } error) return new CachedModInfo(null, error);
        var entry = found.Entry!.Value;

        // 3. Сама запись: локальный заголовок и сжатые данные. Размеры — всегда из каталога: с битом 3 (data descriptor)
        // в локальном заголовке стоят нули.
        if ((entry.Flags & 1) != 0) return Corrupt; // зашифровано
        if (entry.CompressedSize == uint.MaxValue || entry.Size == uint.MaxValue || entry.LocalOffset == uint.MaxValue)
            throw new NeedWholeFile();
        if (entry.Method is not (0 or 8)) throw new NeedWholeFile(); // deflate64 и прочее — пусть разбирает ZipArchive
        if (entry.Size > MaxModInfoSize || entry.CompressedSize > MaxModInfoSize + 64 * 1024)
            return Corrupt; // защита от zip-бомбы: modinfo.json в мегабайт не бывает

        var local = shift + entry.LocalOffset;
        if (local + LocalSize > cdStart) throw new InvalidDataException("local header outside the data");
        // имя и extra в локальном заголовке могут отличаться от каталога — запас в 1 КБ, при нехватке дочитаем
        var guess = Math.Min(cdStart, local + LocalSize + entry.NameLength + entry.ExtraLength + entry.CompressedSize + 1024);
        var head = await reader.GetAsync(local, local + LocalSize, ct, readAhead: guess).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != LocalSig) throw new InvalidDataException("no local header");
        var dataStart = local + LocalSize + U16(head, 26) + U16(head, 28);
        var dataEnd = dataStart + entry.CompressedSize;
        if (dataEnd > cdStart) throw new InvalidDataException("entry data outside the data");
        var data = await reader.GetAsync(dataStart, dataEnd, ct).ConfigureAwait(false);

        // 4. Распаковать и сверить контрольную сумму: не сошлась — архив битый (или поменялся по ходу чтения).
        var bytes = Inflate(data, entry);
        if (Crc32(bytes) != entry.Crc) throw new InvalidDataException("crc mismatch");
        using var text = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return new CachedModInfo(await text.ReadToEndAsync(ct).ConfigureAwait(false), null);
    }

    /// <summary>Позиция подписи конца каталога в хвосте; ищется с конца, комментарий должен уместиться в файл.</summary>
    private static int FindEocd(byte[] tail)
    {
        for (var i = tail.Length - EocdSize; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EocdSig && i + EocdSize + U16(tail, i + 20) <= tail.Length)
                return i;
        return -1;
    }

    private readonly record struct CentralEntry(int Flags, int Method, uint Crc, long CompressedSize, long Size,
        int NameLength, int ExtraLength, long LocalOffset);

    /// <summary>
    /// modinfo.json в корне без учёта регистра — первая такая запись, как у <see cref="Mods.ModScanner.ReadZip"/>; нет —
    /// ключ ошибки: вложенный (имя кончается на «/modinfo.json» или «\modinfo.json») или никакого.
    /// </summary>
    private static (CentralEntry? Entry, string? ErrorKey) FindModInfo(byte[] cd, int count)
    {
        var nested = false;
        var at = 0;
        for (var n = 0; n < count; n++)
        {
            if (at + CentralSize > cd.Length || BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(at)) != CentralSig)
                throw new InvalidDataException("broken central directory");
            var r = cd.AsSpan(at);
            var nameLength = U16(r, 28);
            var extraLength = U16(r, 30);
            var commentLength = U16(r, 32);
            if (at + CentralSize + nameLength + extraLength + commentLength > cd.Length)
                throw new InvalidDataException("broken central directory");

            // имена не в UTF-8 (без бита 11) — CP437; для «modinfo.json» в ASCII разницы нет, Latin1 тут хватает
            var flags = U16(r, 8);
            var nameBytes = r.Slice(CentralSize, nameLength);
            var name = (flags & 0x800) != 0 ? Encoding.UTF8.GetString(nameBytes) : Encoding.Latin1.GetString(nameBytes);
            if (name.Equals(ModInfoFile, StringComparison.OrdinalIgnoreCase))
                return (new CentralEntry(flags, U16(r, 10), BinaryPrimitives.ReadUInt32LittleEndian(r[16..]), U32(r, 20), U32(r, 24),
                    nameLength, extraLength, U32(r, 42)), null);
            if (name.EndsWith("/" + ModInfoFile, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("\\" + ModInfoFile, StringComparison.OrdinalIgnoreCase))
                nested = true;

            at += CentralSize + nameLength + extraLength + commentLength;
        }
        return (null, nested ? "scan.nestedModInfo" : "scan.noModInfo");
    }

    private static byte[] Inflate(byte[] data, CentralEntry entry)
    {
        if (entry.Method == 0)
        {
            if (data.Length != entry.Size) throw new InvalidDataException("stored size mismatch");
            return data;
        }
        var result = new byte[entry.Size];
        using var deflate = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
        var read = 0;
        while (read < result.Length && deflate.Read(result, read, result.Length - read) is var got and > 0) read += got;
        // меньше обещанного — битый; больше — тоже (распакованный размер из каталога — граница от zip-бомбы)
        if (read != result.Length || deflate.Read(new byte[1], 0, 1) != 0) throw new InvalidDataException("size mismatch");
        return result;
    }

    private static int U16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);

    private static long U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);

    private static int U16(byte[] b, int at) => U16(b.AsSpan(), at);

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = uint.MaxValue;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    /// <summary>Архив надо скачать целиком (Zip64, многотомный, необычный метод, файл поменялся по ходу).</summary>
    private sealed class NeedWholeFile : Exception;

    /// <summary>
    /// Прочитанные куски файла и правило, как дочитывать: если до конца файла осталось не больше нужного плюс
    /// <see cref="IRangeSource.ReadThroughLimit"/>, читаем до конца (без обрыва), иначе — ровно нужное, с обрывом.
    /// </summary>
    private sealed class Reader(IRangeSource source)
    {
        private readonly List<(long Start, byte[] Data)> _pieces = [];

        public long Length => source.Length;

        /// <summary>Байты <c>[start, end)</c>; если читать по сети — не меньше чем до <paramref name="readAhead"/>.</summary>
        public async Task<byte[]> GetAsync(long start, long end, CancellationToken ct, long readAhead = 0)
        {
            foreach (var (pieceStart, data) in _pieces)
                if (pieceStart <= start && end <= pieceStart + data.Length)
                    return data.AsSpan((int)(start - pieceStart), (int)(end - start)).ToArray();

            var want = Math.Max(end, Math.Min(readAhead, Length));
            var to = Length - want <= Math.Max(0, source.ReadThroughLimit) ? Length : want;
            if (to - start > int.MaxValue) to = want;
            if (to - start > int.MaxValue) throw new NeedWholeFile();

            var count = (int)(to - start);
            var read = await source.ReadAsync(start, count, ct).ConfigureAwait(false);
            if (read.Length != count) throw new NeedWholeFile(); // файл короче, чем в листинге: поменялся — читаем как есть
            _pieces.Add((start, read));
            return read.AsSpan(0, (int)(end - start)).ToArray();
        }
    }
}

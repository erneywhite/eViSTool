using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// Чтение modinfo.json из архива на сервере кусками: сколько передач и байт уходит, когда передача обрывается,
/// когда архив качается целиком, и что результат совпадает с локальным сканером.
/// </summary>
public sealed class ZipProbeTests : IDisposable
{
    private const string Json = """{ "type": "code", "modid": "carryon", "name": "Carry On", "version": "1.8.0" }""";
    private const int Padding = 2 * 1024 * 1024;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evistool-zipprobe-" + Guid.NewGuid().ToString("N"));

    public ZipProbeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---------- источник со счётчиком

    /// <summary>Файл в памяти: считает передачи, байты, обрывы (чтение кончилось раньше конца файла) и полные скачивания.</summary>
    private sealed class Source(byte[] data, long readThrough = ZipProbe.DefaultReadThroughLimit) : IRangeSource
    {
        public int Reads { get; private set; }
        public long BytesRead { get; private set; }
        public int Cuts { get; private set; }
        public int Opened { get; private set; }
        public bool RangeUnsupported { get; init; }
        public long? FakeLength { get; init; }
        public Exception? FailReads { get; init; }

        public long Length => FakeLength ?? data.Length;
        public long ReadThroughLimit => readThrough;

        public Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct)
        {
            if (FailReads is { } fail) throw fail;
            if (RangeUnsupported) throw new RemoteFilesException(RemoteFailure.RangeUnsupported, "no REST");
            Assert.True(offset >= 0 && count > 0, $"bad range {offset}+{count}");
            Reads++;
            if (this.CutsShort(offset, count)) Cuts++;
            var piece = data.AsSpan((int)Math.Min(offset, data.Length), (int)Math.Min(count, Math.Max(0, data.Length - offset))).ToArray();
            BytesRead += piece.Length;
            return Task.FromResult(piece);
        }

        public Task<Stream> OpenAllAsync(CancellationToken ct)
        {
            Opened++;
            return Task.FromResult<Stream>(new MemoryStream(data, writable: false));
        }
    }

    private static Task<CachedModInfo> Probe(IRangeSource source) => ZipProbe.ReadModInfoAsync(source, CancellationToken.None);

    // ---------- архивы

    private static byte[] Noise(int size, int seed = 1)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] Text(string text, bool bom = true) => [.. (bom ? Encoding.UTF8.GetPreamble() : []), .. Encoding.UTF8.GetBytes(text)];

    /// <summary>Архив через ZipArchive; поток без перемотки — записи с битом 3 (data descriptor), как у потоковых упаковщиков.</summary>
    private static byte[] Zip(IEnumerable<(string Name, byte[] Data, CompressionLevel Level)> files, bool streaming = false)
    {
        var buffer = new MemoryStream();
        Stream target = streaming ? new WriteOnly(buffer) : buffer;
        using (var zip = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data, level) in files)
            {
                using var s = zip.CreateEntry(name, level).Open();
                s.Write(data);
            }
        return buffer.ToArray();
    }

    private sealed class WriteOnly(Stream inner) : Stream
    {
        private long _position;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            _position += count;
        }
    }

    public enum Where { First, Middle, Last }

    /// <summary>Большой мод: 2 МБ несжимаемых данных (два куска) и modinfo.json в заданном месте.</summary>
    private static byte[] BigMod(Where where, CompressionLevel level, string modInfoName = "modinfo.json", bool streaming = false)
    {
        var info = (modInfoName, Text(Json), level);
        var a = ("assets/a.bin", Noise(Padding / 2, 1), CompressionLevel.NoCompression);
        var b = ("assets/b.bin", Noise(Padding / 2, 2), CompressionLevel.NoCompression);
        return Zip(where switch
        {
            Where.First => [info, a, b],
            Where.Middle => [a, info, b],
            _ => [a, b, info],
        }, streaming);
    }

    // ---------- сеть: сколько и как читаем

    [Theory]
    [InlineData(Where.First, CompressionLevel.Optimal)]
    [InlineData(Where.Middle, CompressionLevel.Optimal)]
    [InlineData(Where.Last, CompressionLevel.Optimal)]
    [InlineData(Where.First, CompressionLevel.NoCompression)]
    [InlineData(Where.Middle, CompressionLevel.NoCompression)]
    [InlineData(Where.Last, CompressionLevel.NoCompression)]
    public async Task BigArchive_ReadsFewSmallPieces(Where where, CompressionLevel level)
    {
        var zip = BigMod(where, level);
        var source = new Source(zip);

        var info = await Probe(source);

        Assert.Equal(Json, info.ModInfoJson); // BOM снят
        Assert.Null(info.ErrorKey);
        Assert.Equal(0, source.Opened);
        Assert.InRange(source.Reads, 1, 3);
        Assert.True(source.BytesRead < zip.Length / 5, $"{source.BytesRead} of {zip.Length}");
        Assert.True(source.Cuts <= 1);
    }

    [Fact]
    public async Task ModInfoAtTheEnd_TakesOneReadWithoutCut()
    {
        var source = new Source(BigMod(Where.Last, CompressionLevel.Optimal));

        Assert.Equal(Json, (await Probe(source)).ModInfoJson);

        Assert.Equal(1, source.Reads); // хвост с каталогом и самой записью
        Assert.Equal(0, source.Cuts);
    }

    [Fact]
    public async Task ModInfoFarFromTheEnd_CutsOnlyTheEntryRead()
    {
        var source = new Source(BigMod(Where.First, CompressionLevel.Optimal));

        Assert.Equal(Json, (await Probe(source)).ModInfoJson);

        Assert.Equal(2, source.Reads); // хвост до конца файла, потом одна запись с обрывом
        Assert.Equal(1, source.Cuts);
    }

    [Fact]
    public async Task ModInfoCloseToTheEnd_ReadsThroughInsteadOfCutting()
    {
        // modinfo.json в 300 КБ от конца: дочитать дешевле, чем обрывать передачу и переподключаться
        var zip = Zip([("assets/a.bin", Noise(Padding, 1), CompressionLevel.NoCompression),
            ("modinfo.json", Text(Json), CompressionLevel.Optimal),
            ("assets/b.bin", Noise(300 * 1024, 2), CompressionLevel.NoCompression)]);
        var source = new Source(zip);

        Assert.Equal(Json, (await Probe(source)).ModInfoJson);

        Assert.Equal(2, source.Reads);
        Assert.Equal(0, source.Cuts);
    }

    [Fact]
    public async Task FreeCuts_ReadExactlyWhatIsNeeded()
    {
        // источник без цены обрыва (файл на диске): лишнего не дочитываем
        var zip = BigMod(Where.Middle, CompressionLevel.Optimal);
        var source = new Source(zip, readThrough: 0);

        Assert.Equal(Json, (await Probe(source)).ModInfoJson);

        Assert.InRange(source.Reads, 1, 3);
        Assert.True(source.BytesRead < ZipProbe.TailSize + 4096, $"{source.BytesRead}");
    }

    [Fact]
    public async Task LargeCentralDirectory_IsReadToTheEnd()
    {
        // каталог больше хвоста: 3000 записей с длинными именами
        var files = Enumerable.Range(0, 3000)
            .Select(i => ($"assets/game/textures/block/some-rather-long-name-{i:D5}.json", "{}"u8.ToArray(), CompressionLevel.Optimal))
            .Append(("modinfo.json", Text(Json), CompressionLevel.Optimal));
        var zip = Zip(files);
        Assert.True(zip.Length > ZipProbe.WholeFileLimit);
        var source = new Source(zip);

        Assert.Equal(Json, (await Probe(source)).ModInfoJson);

        Assert.Equal(0, source.Opened);
        Assert.InRange(source.Reads, 1, 3);
        Assert.Equal(0, source.Cuts);
    }

    [Fact]
    public async Task SmallArchive_IsDownloadedWhole()
    {
        var zip = Zip([("modinfo.json", Text(Json), CompressionLevel.Optimal)]);
        var source = new Source(zip);

        Assert.Equal(Json, (await Probe(source)).ModInfoJson);

        Assert.Equal(1, source.Opened);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task NoRangeReads_FallsBackToWholeFile()
    {
        var source = new Source(BigMod(Where.Middle, CompressionLevel.Optimal)) { RangeUnsupported = true };

        Assert.Equal(Json, (await Probe(source)).ModInfoJson);

        Assert.Equal(1, source.Opened);
    }

    [Fact]
    public async Task FileShorterThanListed_FallsBackToWholeFile()
    {
        // файл поменялся между листингом и чтением: кусок пришёл короче — читаем то, что есть на самом деле
        var zip = BigMod(Where.Middle, CompressionLevel.Optimal);
        var source = new Source(zip) { FakeLength = zip.Length + 1000 };

        Assert.Equal(Json, (await Probe(source)).ModInfoJson);

        Assert.Equal(1, source.Opened);
    }

    [Fact]
    public async Task NetworkFailure_IsNotACorruptArchive()
    {
        var source = new Source(BigMod(Where.Middle, CompressionLevel.Optimal))
        {
            FailReads = new RemoteFilesException(RemoteFailure.Connect, "gone"),
        };

        await Assert.ThrowsAsync<RemoteFilesException>(() => Probe(source));
        Assert.Equal(0, source.Opened);
    }

    [Fact]
    public async Task Cancellation_PassesThrough()
    {
        var source = new Source(BigMod(Where.Middle, CompressionLevel.Optimal)) { FailReads = new OperationCanceledException() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Probe(source));
    }

    // ---------- устройство архива

    [Fact]
    public async Task DataDescriptor_SizesComeFromCentralDirectory()
    {
        var zip = BigMod(Where.First, CompressionLevel.Optimal, streaming: true);
        Assert.NotEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(6)) & 8); // бит 3 в локальном заголовке

        var source = new Source(zip);
        Assert.Equal(Json, (await Probe(source)).ModInfoJson);
        Assert.Equal(0, source.Opened);
    }

    [Theory]
    [InlineData("ModInfo.json")]
    [InlineData("MODINFO.JSON")]
    public async Task ModInfoName_IgnoresCase(string name)
    {
        Assert.Equal(Json, (await Probe(new Source(BigMod(Where.Middle, CompressionLevel.Optimal, name)))).ModInfoJson);
    }

    [Fact]
    public async Task WithoutBom_TextIsTheSame()
    {
        var zip = Zip([("big.bin", Noise(Padding), CompressionLevel.NoCompression), ("modinfo.json", Text(Json, bom: false), CompressionLevel.Optimal)]);
        Assert.Equal(Json, (await Probe(new Source(zip))).ModInfoJson);
    }

    [Fact]
    public async Task NestedOrMissing_GiveErrorKeys()
    {
        var nested = Zip([("big.bin", Noise(Padding), CompressionLevel.NoCompression), ("carryon/modinfo.json", Text(Json), CompressionLevel.Optimal)]);
        var missing = Zip([("big.bin", Noise(Padding), CompressionLevel.NoCompression), ("readme.txt", Text("x"), CompressionLevel.Optimal)]);

        Assert.Equal(new CachedModInfo(null, "scan.nestedModInfo"), await Probe(new Source(nested)));
        Assert.Equal(new CachedModInfo(null, "scan.noModInfo"), await Probe(new Source(missing)));
    }

    [Fact]
    public async Task BackslashNested_IsNested()
    {
        var zip = new RawZip { Pad = Padding }.Add("carryon\\modinfo.json", Text(Json)).Build();
        Assert.Equal("scan.nestedModInfo", (await Probe(new Source(zip))).ErrorKey);
    }

    [Fact]
    public async Task LongComment_IsSkipped()
    {
        var source = new Source(new RawZip { Pad = Padding, Comment = 65_000 }.Add("modinfo.json", Text(Json)).Build());

        Assert.Equal(Json, (await Probe(source)).ModInfoJson);
        Assert.Equal(0, source.Opened);
    }

    [Fact]
    public async Task DataBeforeArchive_IsAccountedFor()
    {
        // самораспаковывающийся архив: смещения в каталоге — от начала архива, а не файла
        var zip = new RawZip { Pad = Padding }.Add("modinfo.json", Text(Json)).Build();
        var source = new Source([.. Noise(400 * 1024, 7), .. zip]);

        Assert.Equal(Json, (await Probe(source)).ModInfoJson);
        Assert.Equal(0, source.Opened);
    }

    [Fact]
    public async Task BrokenCentralDirectory_IsCorrupt()
    {
        var zip = BigMod(Where.Middle, CompressionLevel.Optimal);
        var eocd = zip.Length - 22;
        var cd = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(eocd + 16));
        zip[cd] = 0; // подпись первой записи каталога испорчена

        var source = new Source(zip);
        Assert.Equal("scan.corrupt", (await Probe(source)).ErrorKey);
        Assert.Equal(0, source.Opened);
        Assert.Equal("scan.corrupt", (await Probe(new Source(zip, readThrough: 0) { RangeUnsupported = true })).ErrorKey); // ZipArchive думает так же
    }

    [Fact]
    public async Task NoEndOfDirectory_IsCorrupt()
    {
        Assert.Equal("scan.corrupt", (await Probe(new Source(Noise(Padding)))).ErrorKey);
    }

    [Fact]
    public async Task DamagedEntryData_IsCorrupt()
    {
        // данные записи испорчены — контрольная сумма не сходится
        var zip = new RawZip { Pad = Padding }.Add("modinfo.json", Text(Json)).Build();
        zip[RawZip.FirstDataAt("modinfo.json") + 5] ^= 0xFF;

        Assert.Equal("scan.corrupt", (await Probe(new Source(zip))).ErrorKey);
    }

    [Fact]
    public async Task Encrypted_IsCorrupt()
    {
        var source = new Source(new RawZip { Pad = Padding }.Add("modinfo.json", Text(Json), flags: 1).Build());

        Assert.Equal("scan.corrupt", (await Probe(source)).ErrorKey);
        Assert.Equal(0, source.Opened);
    }

    [Fact]
    public async Task UnknownMethod_FallsBackToZipArchive()
    {
        // метод 14 (LZMA): разбирать не нам — качаем целиком, ZipArchive его тоже не знает
        var source = new Source(new RawZip { Pad = Padding }.Add("modinfo.json", Text(Json), method: 14).Build());

        Assert.Equal("scan.corrupt", (await Probe(source)).ErrorKey);
        Assert.Equal(1, source.Opened);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Zip64_FallsBackToWholeFile(bool locator)
    {
        var raw = new RawZip { Pad = Padding, Zip64Locator = locator, Zip64Offset = !locator };
        var source = new Source(raw.Add("modinfo.json", Text(Json)).Build());

        await Probe(source);

        Assert.Equal(1, source.Opened);
    }

    [Fact]
    public async Task ZipBomb_IsCorrupt_WithoutReadingIt()
    {
        var bomb = Zip([("big.bin", Noise(300 * 1024), CompressionLevel.NoCompression),
            ("modinfo.json", new byte[ZipProbe.MaxModInfoSize + 1], CompressionLevel.SmallestSize)]);
        var source = new Source(bomb);

        Assert.Equal("scan.corrupt", (await Probe(source)).ErrorKey);
        Assert.Equal(1, source.Reads); // распакованный размер виден из каталога — до данных дело не доходит
    }

    [Fact]
    public async Task ZipBomb_SmallFile_IsCorrupt()
    {
        var bomb = Zip([("modinfo.json", new byte[ZipProbe.MaxModInfoSize + 1], CompressionLevel.SmallestSize)]);
        Assert.Equal("scan.corrupt", (await Probe(new Source(bomb))).ErrorKey);
    }

    [Fact]
    public async Task SizeLie_IsCorrupt()
    {
        // каталог обещает меньше, чем распаковывается: граница от zip-бомбы не обходится враньём в каталоге
        var zip = new RawZip { Pad = Padding }.Add("modinfo.json", Text(Json), method: 8, sizeDelta: -10).Build();
        Assert.Equal("scan.corrupt", (await Probe(new Source(zip))).ErrorKey);
    }

    // ---------- совпадение с локальным сканером

    public static TheoryData<string> RealZips()
    {
        var root = RepoRoot();
        var data = new TheoryData<string>();
        foreach (var dir in new[] { "tests/eViSTool.Core.Tests/TestData", "tests/snapshots/fixture" })
            foreach (var zip in Directory.EnumerateFiles(Path.Combine(root, dir), "*.zip", SearchOption.AllDirectories).Order())
                data.Add(Path.GetRelativePath(root, zip));
        return data;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "eViSTool.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("eViSTool.slnx");
    }

    [Theory]
    [MemberData(nameof(RealZips))]
    public async Task RealZip_MatchesLocalScanner_WholeAndInPieces(string relative)
    {
        var path = Path.Combine(RepoRoot(), relative);
        await AssertSameAsScanner(path);

        // тот же архив, но большой: в начало и в конец добавлены несжимаемые данные — читается кусками
        using var original = ZipFile.OpenRead(path);
        var entries = original.Entries.Select(e =>
        {
            using var s = e.Open();
            var copy = new MemoryStream();
            s.CopyTo(copy);
            return (e.FullName, copy.ToArray(), CompressionLevel.Optimal);
        }).ToList();
        var big = Path.Combine(_dir, "big-" + Path.GetFileName(path));
        File.WriteAllBytes(big, Zip([("pad/a.bin", Noise(Padding / 2, 3), CompressionLevel.NoCompression), .. entries,
            ("pad/b.bin", Noise(Padding / 2, 4), CompressionLevel.NoCompression)]));
        Assert.Equal(0, await AssertSameAsScanner(big));
    }

    public static TheoryData<string> MadeZips() => ["deflate-first", "stored-last", "nested", "missing", "descriptor", "brokenjson", "nomodid", "evilid", "corruptcd"];

    [Theory]
    [MemberData(nameof(MadeZips))]
    public async Task MadeZip_MatchesLocalScanner(string kind)
    {
        var bytes = kind switch
        {
            "deflate-first" => BigMod(Where.First, CompressionLevel.Optimal),
            "stored-last" => BigMod(Where.Last, CompressionLevel.NoCompression),
            "nested" => Zip([("big.bin", Noise(Padding), CompressionLevel.NoCompression), ("x/modinfo.json", Text(Json), CompressionLevel.Optimal)]),
            "missing" => Zip([("big.bin", Noise(Padding), CompressionLevel.NoCompression)]),
            "descriptor" => BigMod(Where.Middle, CompressionLevel.Optimal, streaming: true),
            "brokenjson" => Zip([("big.bin", Noise(Padding), CompressionLevel.NoCompression), ("modinfo.json", Text("{ not json"), CompressionLevel.Optimal)]),
            "nomodid" => Zip([("big.bin", Noise(Padding), CompressionLevel.NoCompression), ("modinfo.json", Text("""{ "name": "x" }"""), CompressionLevel.Optimal)]),
            "evilid" => Zip([("big.bin", Noise(Padding), CompressionLevel.NoCompression), ("modinfo.json", Text("""{ "modid": "../evil", "name": "x", "version": "1" }"""), CompressionLevel.Optimal)]),
            _ => CorruptCd(),
        };
        var path = Path.Combine(_dir, kind + ".zip");
        File.WriteAllBytes(path, bytes);

        Assert.Equal(0, await AssertSameAsScanner(path)); // все они больше 256 КБ — читаются кусками
    }

    private static byte[] CorruptCd()
    {
        var zip = BigMod(Where.Middle, CompressionLevel.Optimal);
        zip[(int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(zip.Length - 22 + 16))] = 0;
        return zip;
    }

    /// <summary>Результат ZipProbe → FromCached тот же, что у ModScanner.ReadZip. Возвращает число полных скачиваний.</summary>
    private static async Task<int> AssertSameAsScanner(string path)
    {
        var expected = ModScanner.ReadZip(path);
        var source = new Source(File.ReadAllBytes(path));
        var actual = ModScanner.FromCached(path, await Probe(source));

        Assert.Equal(expected.Info?.ModId, actual.Info?.ModId);
        Assert.Equal(expected.Info?.Name, actual.Info?.Name);
        Assert.Equal(expected.Info?.Version, actual.Info?.Version);
        Assert.Equal(expected.Error is null, actual.Error is null);
        // язык окна может переключить соседний тест — сравниваем ключ ошибки, а не текст
        foreach (var key in new[] { "scan.noModInfo", "scan.nestedModInfo", "scan.corrupt" })
            Assert.Equal(expected.Error is { } e && HostedTestText.Of(key).Contains(e),
                actual.Error is { } a && HostedTestText.Of(key).Contains(a));
        return source.Opened;
    }

    // ---------- архив руками: то, чего ZipArchive не пишет

    /// <summary>
    /// Архив, собранный побайтно: записи без сжатия с любыми флагами и методом, несжимаемая «набивка» в начале,
    /// комментарий, признаки Zip64. Для записи с методом 8 данные сжимаются по-настоящему.
    /// </summary>
    private sealed class RawZip
    {
        public int Pad { get; init; }
        public int Comment { get; init; }
        public bool Zip64Locator { get; init; }
        public bool Zip64Offset { get; init; }

        private readonly List<(string Name, byte[] Data, int Flags, int Method, int SizeDelta)> _files = [];

        public RawZip Add(string name, byte[] data, int flags = 0, int method = 0, int sizeDelta = 0)
        {
            _files.Add((name, data, flags, method, sizeDelta));
            return this;
        }

        /// <summary>Где начинаются данные первой записи после набивки (у набивки имя «pad.bin», без extra).</summary>
        public static int FirstDataAt(string name) => 30 + "pad.bin".Length + Padding + 30 + name.Length;

        public byte[] Build()
        {
            var all = new List<(string Name, byte[] Data, int Flags, int Method, int SizeDelta)>();
            if (Pad > 0) all.Add(("pad.bin", Noise(Pad, 9), 0, 0, 0));
            all.AddRange(_files);

            var output = new MemoryStream();
            var w = new BinaryWriter(output);
            var central = new MemoryStream();
            var c = new BinaryWriter(central);
            foreach (var (name, data, flags, method, sizeDelta) in all)
            {
                var offset = (uint)output.Position;
                var stored = method == 8 ? Deflate(data) : data;
                var crc = Crc32(data);
                var nameBytes = Encoding.UTF8.GetBytes(name);
                var size = (uint)(data.Length + sizeDelta);

                w.Write(0x04034B50u); w.Write((ushort)20); w.Write((ushort)flags); w.Write((ushort)method);
                w.Write((ushort)0); w.Write((ushort)0x21); w.Write(crc); w.Write((uint)stored.Length); w.Write(size);
                w.Write((ushort)nameBytes.Length); w.Write((ushort)0); w.Write(nameBytes); w.Write(stored);

                c.Write(0x02014B50u); c.Write((ushort)20); c.Write((ushort)20); c.Write((ushort)flags); c.Write((ushort)method);
                c.Write((ushort)0); c.Write((ushort)0x21); c.Write(crc); c.Write((uint)stored.Length); c.Write(size);
                c.Write((ushort)nameBytes.Length); c.Write((ushort)0); c.Write((ushort)0); c.Write((ushort)0); c.Write((ushort)0);
                c.Write(0u); c.Write(offset); c.Write(nameBytes);
            }
            var cdOffset = (uint)output.Position;
            w.Write(central.ToArray());
            if (Zip64Locator)
            {
                w.Write(0x07064B50u); w.Write(0u); w.Write(0ul); w.Write(1u); // указатель на запись Zip64 (самой записи нет)
            }
            w.Write(0x06054B50u); w.Write((ushort)0); w.Write((ushort)0);
            w.Write((ushort)all.Count); w.Write((ushort)all.Count);
            w.Write((uint)central.Length); w.Write(Zip64Offset ? uint.MaxValue : cdOffset);
            w.Write((ushort)Comment); w.Write(Enumerable.Repeat((byte)'#', Comment).ToArray());
            w.Flush();
            return output.ToArray();
        }

        private static byte[] Deflate(byte[] data)
        {
            var buffer = new MemoryStream();
            using (var d = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true)) d.Write(data);
            return buffer.ToArray();
        }

        private static uint Crc32(byte[] data)
        {
            var crc = uint.MaxValue;
            foreach (var b in data)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
            return ~crc;
        }
    }
}

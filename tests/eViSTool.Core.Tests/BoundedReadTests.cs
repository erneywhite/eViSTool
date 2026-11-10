using System.IO.Compression;
using eViSTool.Core.Mods;
using eViSTool.Core.Platform;

namespace eViSTool.Core.Tests;

/// <summary>Аудит 4.7: записи архива читаются с пределом — сжатый «пустой» гигант не съест память.</summary>
public sealed class BoundedReadTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-bounded-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Zip(string name, long size)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        var chunk = new byte[1 << 16];
        Array.Fill(chunk, (byte)' ');
        for (long done = 0; done < size; done += chunk.Length) stream.Write(chunk, 0, (int)Math.Min(chunk.Length, size - done));
        return path;
    }

    [Fact]
    public void TooBigEntry_IsRefused_SmallOneIsRead()
    {
        using (var zip = ZipFile.OpenRead(Zip("modinfo.json", BoundedRead.TextLimit + 1)))
            Assert.Throws<InvalidDataException>(() => BoundedRead.Text(zip.Entries[0]));
        using (var zip = ZipFile.OpenRead(Zip("modinfo.json", 100)))
            Assert.Equal(100, BoundedRead.Text(zip.Entries[0]).Length);

        var dest = Path.Combine(_root, "out.bin");
        using (var zip = ZipFile.OpenRead(Zip("x.zip", 2000)))
            Assert.Throws<InvalidDataException>(() => BoundedRead.ExtractToFile(zip.Entries[0], dest, max: 1000));
        Assert.False(File.Exists(dest)); // недоизвлечённый файл не остаётся
    }

    [Fact]
    public void Scanner_ReportsAHugeModInfo_InsteadOfReadingIt()
    {
        var mod = ModScanner.ReadZip(Zip("modinfo.json", BoundedRead.TextLimit + 1));
        Assert.Null(mod.Info);
        Assert.NotNull(mod.Error);
    }
}

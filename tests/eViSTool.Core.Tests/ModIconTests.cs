using System.IO.Compression;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;

namespace eViSTool.Core.Tests;

/// <summary>
/// Иконка мода (docs/design.md, раздел 7): modicon.png из корня архива или папки мода, без учёта регистра имени;
/// вложенная, битый архив, чужие файлы — иконки нет. Плюс отметка файла от сканера папки и логотип модбазы без её
/// стандартной картинки.
/// </summary>
public sealed class ModIconTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-tests-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // не картинка, а узнаваемые байты: читатель отдаёт их как есть, декодирует окно
    private static readonly byte[] Icon = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4, 5];

    private string Zip(string name, params (string Entry, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open()))
            w.Write("""{ "modid": "alpha", "name": "Alpha", "version": "1.0.0" }""");
        foreach (var (entry, bytes) in entries)
            using (var s = zip.CreateEntry(entry).Open())
                s.Write(bytes);
        return path;
    }

    [Fact]
    public void Reads_icon_from_zip_root()
    {
        Assert.Equal(Icon, ModIcon.Read(Zip("alpha.zip", ("modicon.png", Icon))));
        // имя — без учёта регистра: так его пишут не все авторы
        Assert.Equal(Icon, ModIcon.Read(Zip("upper.zip", ("ModIcon.PNG", Icon))));
        // точное имя важнее похожего
        Assert.Equal(Icon, ModIcon.Read(Zip("both.zip", ("MODICON.png", [9, 9]), ("modicon.png", Icon))));
    }

    [Fact]
    public void Nested_or_missing_icon_is_none()
    {
        Assert.Null(ModIcon.Read(Zip("nested.zip", ("assets/alpha/modicon.png", Icon))));
        Assert.Null(ModIcon.Read(Zip("bare.zip")));
        Assert.Null(ModIcon.Read(Zip("empty.zip", ("modicon.png", []))));
    }

    [Fact]
    public void Reads_icon_from_mod_folder()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "alpha")).FullName;
        Assert.Null(ModIcon.Read(dir));
        File.WriteAllBytes(Path.Combine(dir, "ModIcon.png"), Icon);
        Assert.Equal(Icon, ModIcon.Read(dir));
    }

    [Fact]
    public void Broken_strange_or_missing_file_is_none()
    {
        var broken = Path.Combine(_root, "broken.zip");
        File.WriteAllBytes(broken, "PK\u0003\u0004 не архив"u8.ToArray());
        Assert.Null(ModIcon.Read(broken));

        var code = Path.Combine(_root, "alpha.dll");
        File.WriteAllBytes(code, Icon);
        Assert.Null(ModIcon.Read(code)); // код-мод: иконки у него не бывает
        Assert.Null(ModIcon.Read(Path.Combine(_root, "gone.zip")));
    }

    [Fact]
    public void Oversized_icon_is_none()
    {
        // нули жмутся в архиве до килобайтов, а распакованные больше предела
        var huge = new byte[ModIcon.MaxBytes + 1];
        Assert.Null(ModIcon.Read(Zip("huge.zip", ("modicon.png", huge))));
    }

    [Fact]
    public void Png_size_comes_from_header()
    {
        byte[] header = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 1, 0xE0, 0, 0, 1, 0x40]; // 480 × 320
        Assert.Equal((480, 320), ModIcon.PngSize(header));
        Assert.Null(ModIcon.PngSize(header[..20]));
        Assert.Null(ModIcon.PngSize(Icon));
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[20]];
        Assert.Null(ModIcon.PngSize(jpeg)); // JPEG под именем .png
    }

    [Fact]
    public void Scanner_stamps_files_and_a_rewrite_changes_the_stamp()
    {
        var mods = Directory.CreateDirectory(Path.Combine(_root, "Mods")).FullName;
        var zip = Path.Combine(mods, "alpha_1.0.0.zip");
        File.Move(Zip("alpha_1.0.0.zip", ("modicon.png", Icon)), zip);
        Directory.CreateDirectory(Path.Combine(mods, "beta"));
        File.WriteAllText(Path.Combine(mods, "beta", "modinfo.json"), """{ "modid": "beta", "version": "1.0.0" }""");

        var scanned = ModScanner.Scan(mods);
        var alpha = scanned.Single(m => m.Info?.ModId == "alpha");
        Assert.Equal(new ModFileStamp(zip, File.GetLastWriteTimeUtc(zip), new FileInfo(zip).Length), alpha.Stamp);
        Assert.Equal(alpha.Path, alpha.Stamp!.Path);
        Assert.Equal(Path.Combine(mods, "beta"), scanned.Single(m => m.Info?.ModId == "beta").Stamp?.Path);

        // мод обновили на месте: путь тот же, отметка другая — окно прочитает иконку заново
        File.WriteAllBytes(zip, [.. File.ReadAllBytes(zip), 0]);
        File.SetLastWriteTimeUtc(zip, alpha.Stamp.WriteTimeUtc.AddMinutes(1));
        Assert.NotEqual(alpha.Stamp, ModScanner.Scan(mods).Single(m => m.Info?.ModId == "alpha").Stamp);
    }

    [Fact]
    public void ModDb_default_picture_is_not_a_logo()
    {
        Assert.Equal("https://moddbcdn.vintagestory.at/carryon_1.png", ModDbLogo.Usable(" https://moddbcdn.vintagestory.at/carryon_1.png "));
        Assert.Null(ModDbLogo.Usable("https://moddbcdn.vintagestory.at/mod-default_a8110a8541b95c5592b109c5b569df79.png"));
        Assert.Null(ModDbLogo.Usable("https://mods.vintagestory.at/web/img/Mod-Default.png"));
        Assert.Null(ModDbLogo.Usable(""));
        Assert.Null(ModDbLogo.Usable(null));
    }
}

using System.Text;
using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// «Настройки модов» сервера у хостера (<see cref="HostedModConfigSource"/>) на поддельном FTP «как G-Portal»: список
/// с привязкой к модам, чтение, запись с проверкой «поменяли с момента открытия», запись через bak и сбой на каждом
/// шаге (файл не теряется), шаг назад и сброс с копиями на этой машине, пути за пределы ModConfig.
/// </summary>
public sealed class HostedModConfigsTests : IDisposable
{
    private const string Carry = "/ModConfig/carryon.json";
    private const string CarryText = "{\n  \"Enabled\": true,\n  \"Range\": 3\n}\n";
    private const string Tmp = Carry + ".evistool.tmp";
    private const string Bak = Carry + ".evistool.bak";

    private readonly HostedFixture _host;

    public HostedModConfigsTests() : this(gportal: true) { }

    private HostedModConfigsTests(bool gportal)
    {
        _host = new HostedFixture(gportal);
        _host.GPortalLayout();
        _host.Server.Put("/Mods/carryon_1.0.0.zip", HostedFixture.ZipBytes("carryon", "1.0.0", "Carry On"));
        _host.Server.Put("/Mods/automap_5.0.3.zip", HostedFixture.ZipBytes("egocaribautomapmarkers", "5.0.3", "Auto Map Markers"));
        _host.Server.Put(Carry, CarryText);
        _host.Server.Put("/ModConfig/AutoMapMarkers/settings.json", "{ \"Markers\": [] }");
        _host.Server.Put("/ModConfig/unknown.cfg", "x = 1");
        _host.Server.Put("/ModConfig/icon.png", [1, 2, 3]);                     // не текст — не показываем
        _host.Server.Put("/ModConfig/old.json.evistool.tmp", "{}");            // наш временный файл — не настройка
    }

    public void Dispose() => _host.Dispose();

    private HostedModConfigSource Source() => new(_host.Target);

    private string SwapDir => Path.Combine(HostedJournal.DirFor(_host.Profile.Id), "modconfig-swap");

    private bool HasSwapMarks => Directory.Exists(SwapDir) && Directory.EnumerateFiles(SwapDir).Any();

    /// <summary>Текст — перевод ключа с подстановкой (на любом языке: соседний тест может сменить язык).</summary>
    private static void AssertText(string key, string actual, params object[] args) =>
        Assert.Contains(actual, HostedTestText.Of(key).Select(t => string.Format(t, args)));

    /// <summary>Источник, который уже прочитал список (как вкладка: сначала список, потом файл).</summary>
    private async Task<HostedModConfigSource> OpenedAsync()
    {
        var source = Source();
        await source.ListAsync();
        return source;
    }

    // ---------- список

    [Fact]
    public async Task List_BindsFilesToTheModsOfTheLastScan_AndSkipsNonText()
    {
        await ModTargets.ScanAsync(_host.Target);
        var lists = _host.Server.Calls["list"];

        var files = await Source().ListAsync();

        Assert.Equal(["AutoMapMarkers/settings.json", "carryon.json", "unknown.cfg"], files.Select(f => f.RelativePath));
        Assert.Equal(["egocaribautomapmarkers", "carryon", null], files.Select(f => f.ModId));
        Assert.Equal(["Auto Map Markers", "Carry On", null], files.Select(f => f.ModName));
        Assert.Equal([ModConfigKind.Json, ModConfigKind.Json, ModConfigKind.Text], files.Select(f => f.Kind));
        Assert.Equal(Encoding.UTF8.GetByteCount(CarryText), files[1].Size);
        // моды не сканируются заново: корень, ModConfig и его подпапка
        Assert.Equal(3, _host.Server.Calls["list"] - lists);
        Assert.Empty(_host.Server.Writes);
        Assert.Equal(["AutoMapMarkers/settings.json", "carryon.json", "unknown.cfg"], HostedModConfigSource.LastListed(_host.Profile.Id)!);
    }

    [Fact]
    public async Task List_WithoutAScanInThisWindow_ScansTheModsFirst()
    {
        Assert.Null(HostedMods.LastScan(_host.Target));

        var files = await Source().ListAsync();

        Assert.Equal("carryon", files.Single(f => f.RelativePath == "carryon.json").ModId);
        Assert.NotNull(HostedMods.LastScan(_host.Target));
    }

    [Fact]
    public async Task List_WithoutModConfigFolder_IsEmpty_AndSaysThereIsNoFolder()
    {
        Directory.Delete(Path.Combine(_host.Server.Root, "ModConfig"), recursive: true);
        var source = Source();

        Assert.Empty(await source.ListAsync());

        Assert.False(source.FolderExists);
        Assert.Equal("/ModConfig", source.Folder);
        Assert.Null(source.LocalFolder);         // «Показать файл» у хостера скрыт
        Assert.Null(source.LocalPath("carryon.json"));
    }

    // ---------- чтение

    [Fact]
    public async Task Read_ReturnsTheText_AndTheListingTime()
    {
        var source = await OpenedAsync();
        var utc = File.GetLastWriteTimeUtc(Path.Combine(_host.Server.Root, "ModConfig", "carryon.json"));

        var content = await source.ReadAsync("carryon.json");

        Assert.Equal(CarryText, content.Text);
        Assert.True(content.Exists);
        Assert.Equal(0, content.Versions);
        // время из листинга (MLSD у G-Portal — UTC до секунды)
        Assert.Equal(new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, DateTimeKind.Utc), content.ChangedUtc);

        var missing = await source.ReadAsync("gone.json");
        Assert.False(missing.Exists);
        Assert.Equal("", missing.Text);
        Assert.Null(missing.ChangedUtc);
    }

    [Fact]
    public async Task Read_WithoutAList_FindsTheFolderItself()
    {
        var content = await Source().ReadAsync("AutoMapMarkers/settings.json");

        Assert.Equal("{ \"Markers\": [] }", content.Text);
        Assert.Equal("AutoMapMarkers/settings.json", content.RelativePath);
    }

    // ---------- запись

    [Fact]
    public async Task Save_GoesThroughBak_KeepsTheOldVersionHere_AndLeavesNoTmpOrBak()
    {
        var source = await OpenedAsync();
        var opened = await source.ReadAsync("carryon.json");
        var text = CarryText.Replace("3", "5");

        var result = await source.SaveAsync(new ModConfigSaveRequest("carryon.json", text, opened.ChangedUtc));

        Assert.False(result.Changed);
        Assert.Equal(text, _host.Server.Text(Carry));
        Assert.Equal(text, result.Content!.Text);
        Assert.Equal(1, result.Content.Versions);
        Assert.NotNull(result.Content.ChangedUtc);
        Assert.False(_host.Server.Has(Tmp));
        Assert.False(_host.Server.Has(Bak));
        Assert.False(HasSwapMarks);
        // G-Portal поверх не переименовывает: прежний — в bak, временный — на место, bak убран
        Assert.Equal([$"upload {Tmp}", $"rename {Carry} → {Bak}", $"rename {Tmp} → {Carry}", $"delete {Bak}"], _host.Server.Writes);
        var backups = ModConfigBackups.ForProfile(_host.Profile);
        Assert.Equal(CarryText, File.ReadAllText(backups.Versions("carryon.json").Single()));
    }

    [Fact]
    public async Task Save_AgainWithTheReturnedTime_IsNotAConflict()
    {
        var source = await OpenedAsync();
        var first = await source.SaveAsync(new ModConfigSaveRequest("carryon.json", "{ \"Range\": 4 }", null));

        var second = await source.SaveAsync(new ModConfigSaveRequest("carryon.json", "{ \"Range\": 6 }", first.Content!.ChangedUtc));

        Assert.False(second.Changed);
        Assert.Equal("{ \"Range\": 6 }", _host.Server.Text(Carry));
        Assert.Equal(2, second.Content!.Versions);
    }

    [Fact]
    public async Task Save_FileChangedSinceItWasOpened_ReturnsChanged_AndWritesNothing()
    {
        var source = await OpenedAsync();
        var opened = await source.ReadAsync("carryon.json");
        // сам мод переписал файл при остановке сервера
        var local = _host.Server.Put(Carry, "{ \"Range\": 9 }");
        File.SetLastWriteTimeUtc(local, DateTime.UtcNow.AddMinutes(2));

        var result = await source.SaveAsync(new ModConfigSaveRequest("carryon.json", "{ \"Range\": 5 }", opened.ChangedUtc));

        Assert.True(result.Changed);
        Assert.Null(result.Content);
        Assert.Equal("{ \"Range\": 9 }", _host.Server.Text(Carry));
        Assert.Empty(_host.Server.Writes);
        Assert.Empty(ModConfigBackups.ForProfile(_host.Profile).Versions("carryon.json"));

        // убрали с момента открытия — тоже «поменяли»
        File.Delete(local);
        Assert.True((await source.SaveAsync(new ModConfigSaveRequest("carryon.json", "{}", opened.ChangedUtc))).Changed);
        Assert.False(_host.Server.Has(Carry));
    }

    [Fact]
    public async Task Save_BrokenJson_IsRefusedBeforeTheServer()
    {
        var source = await OpenedAsync();
        var connects = _host.Server.Connects;
        var calls = _host.Server.Calls.Values.Sum();

        await Assert.ThrowsAsync<InvalidDataException>(() => source.SaveAsync(new ModConfigSaveRequest("carryon.json", "{ \"Range\": ", null)));

        Assert.Equal(CarryText, _host.Server.Text(Carry));
        Assert.Equal(connects, _host.Server.Connects);
        Assert.Equal(calls, _host.Server.Calls.Values.Sum());
    }

    [Fact]
    public async Task Save_KeepsTheBom_IfTheFileHadOne()
    {
        _host.Server.Put(Carry, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{ \"Range\": 3 }")]);
        var source = await OpenedAsync();
        Assert.Equal("{ \"Range\": 3 }", (await source.ReadAsync("carryon.json")).Text); // BOM в текст не попадает

        await source.SaveAsync(new ModConfigSaveRequest("carryon.json", "{ \"Range\": 4 }", null));

        var bytes = File.ReadAllBytes(Path.Combine(_host.Server.Root, "ModConfig", "carryon.json"));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("{ \"Range\": 4 }", Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public async Task Save_NewFileInAMissingFolder_CreatesTheFolders()
    {
        Directory.Delete(Path.Combine(_host.Server.Root, "ModConfig"), recursive: true);
        var source = await OpenedAsync();

        var result = await source.SaveAsync(new ModConfigSaveRequest("NewMod/new.json", "{ \"On\": true }", null));

        Assert.Equal("{ \"On\": true }", _host.Server.Text("/ModConfig/NewMod/new.json"));
        Assert.True(result.Content!.Exists);
        Assert.Equal(0, result.Content.Versions); // прежней версии не было
        Assert.True(source.FolderExists);
        Assert.Equal(["mkdir /ModConfig", "mkdir /ModConfig/NewMod"], _host.Server.Writes.Take(2));
    }

    // ---------- сбой посередине записи: файл не теряется

    [Fact]
    public async Task UploadFails_TheFileStaysAsItWas()
    {
        var source = await OpenedAsync();
        _host.Server.FailOn = (op, path) => op == "upload" && path == Tmp;

        await Assert.ThrowsAsync<RemoteFilesException>(() => source.SaveAsync(new ModConfigSaveRequest("carryon.json", "{}", null)));

        Assert.Equal(CarryText, _host.Server.Text(Carry));
        Assert.False(_host.Server.Has(Tmp));
        Assert.False(_host.Server.Has(Bak));
    }

    [Fact]
    public async Task MovingTheOldFileToBakFails_TheFileStaysAsItWas()
    {
        var source = await OpenedAsync();
        _host.Server.FailOn = (op, path) => op == "rename" && path == Carry;

        await Assert.ThrowsAsync<RemoteFilesException>(() => source.SaveAsync(new ModConfigSaveRequest("carryon.json", "{}", null)));

        Assert.Equal(CarryText, _host.Server.Text(Carry));
        Assert.False(_host.Server.Has(Tmp));
        Assert.False(_host.Server.Has(Bak));
        // отметка остаётся (ответ мог потеряться, а переименование — пройти); следующая операция видит, что файл на
        // месте, и молча её снимает
        var again = Source();
        Assert.Equal(CarryText, (await again.ReadAsync("carryon.json")).Text);
        Assert.Empty(again.Warnings);
        Assert.False(HasSwapMarks);
    }

    [Fact]
    public async Task RenameAfterBakFails_TheOldFileIsPutBack()
    {
        var source = await OpenedAsync();
        _host.Server.FailOn = (op, path) => op == "rename" && path == Tmp && _host.Server.Has(Bak);

        await Assert.ThrowsAsync<RemoteFilesException>(() => source.SaveAsync(new ModConfigSaveRequest("carryon.json", "{}", null)));

        Assert.Equal(CarryText, _host.Server.Text(Carry));
        Assert.False(_host.Server.Has(Tmp));
        Assert.False(_host.Server.Has(Bak));
        Assert.False(HasSwapMarks);
    }

    [Fact]
    public async Task PutBackFailsToo_TheNextOperationReturnsTheFile()
    {
        var source = await OpenedAsync();
        _host.Server.FailOn = (op, path) => op == "rename" && (path == Tmp && _host.Server.Has(Bak) || path == Bak);

        await Assert.ThrowsAsync<RemoteFilesException>(() => source.SaveAsync(new ModConfigSaveRequest("carryon.json", "{}", null)));
        Assert.False(_host.Server.Has(Carry)); // остался только bak
        Assert.True(_host.Server.Has(Bak));
        Assert.True(HasSwapMarks);
        _host.Server.FailOn = null;

        var again = Source();
        var content = await again.ReadAsync("carryon.json");

        Assert.Equal(CarryText, content.Text);
        Assert.Equal(CarryText, _host.Server.Text(Carry));
        Assert.False(_host.Server.Has(Bak));
        Assert.False(HasSwapMarks);
        AssertText("mcfg.hostedRestored", again.Warnings.Single(), "carryon.json");
    }

    [Fact]
    public async Task PutBackFailsToo_TheModsScanReturnsTheFile_AndSaysSo()
    {
        var source = await OpenedAsync();
        _host.Server.FailOn = (op, path) => op == "rename" && (path == Tmp && _host.Server.Has(Bak) || path == Bak);
        await Assert.ThrowsAsync<RemoteFilesException>(() => source.SaveAsync(new ModConfigSaveRequest("carryon.json", "{}", null)));
        _host.Server.FailOn = null;

        var (profile, _) = await ModTargets.ScanAsync(_host.Target);

        Assert.Equal(CarryText, _host.Server.Text(Carry));
        Assert.Contains(HostedTestText.Of("mcfg.hostedRestored").Select(t => string.Format(t, "carryon.json")), w => profile.Warnings.Contains(w));
        Assert.False(HasSwapMarks);
    }

    [Fact]
    public async Task ConnectionDropsBetweenTheRenames_TheNextConnectionReturnsTheFile()
    {
        var source = await OpenedAsync();
        // связь рвётся на переименовании временного файла после bak: вернуть bak по этой же связи уже нельзя
        _host.Server.OnBefore = (op, path) =>
        {
            if (op == "rename" && path == Tmp && _host.Server.Has(Bak)) _host.Server.DropNext = true;
        };

        var ex = await Assert.ThrowsAsync<RemoteFilesException>(() => source.SaveAsync(new ModConfigSaveRequest("carryon.json", "{}", null)));
        Assert.Equal(RemoteFailure.Connect, ex.Kind);
        Assert.False(_host.Server.Has(Carry));
        _host.Server.OnBefore = null;

        var files = await Source().ListAsync();

        Assert.Contains(files, f => f.RelativePath == "carryon.json");
        Assert.Equal(CarryText, _host.Server.Text(Carry));
        Assert.False(_host.Server.Has(Bak));
        Assert.False(_host.Server.Has(Tmp)); // и временный файл убран
        Assert.False(HasSwapMarks);
    }

    // ---------- шаг назад и сброс

    [Fact]
    public async Task Undo_UploadsThePreviousVersionsOneByOne()
    {
        var source = await OpenedAsync();
        await source.SaveAsync(new ModConfigSaveRequest("carryon.json", "{ \"v\": 2 }", null));
        await source.SaveAsync(new ModConfigSaveRequest("carryon.json", "{ \"v\": 3 }", null));

        var back = await source.UndoAsync("carryon.json");

        Assert.Equal("{ \"v\": 2 }", _host.Server.Text(Carry));
        Assert.Equal("{ \"v\": 2 }", back.Text);
        Assert.Equal(1, back.Versions);

        back = await source.UndoAsync("carryon.json");
        Assert.Equal(CarryText, _host.Server.Text(Carry));
        Assert.Equal(0, back.Versions);

        // возвращать больше нечего — ничего и не пишем
        var writes = _host.Server.Writes.Count;
        Assert.Equal(CarryText, (await source.UndoAsync("carryon.json")).Text);
        Assert.Equal(writes, _host.Server.Writes.Count);
        Assert.False(_host.Server.Has(Bak));
    }

    [Fact]
    public async Task Reset_DeletesTheFileOnTheServer_KeepsACopyHere_AndUndoBringsItBack()
    {
        var source = await OpenedAsync();

        var reset = await source.ResetAsync("carryon.json");

        Assert.False(reset.Exists);
        Assert.Equal(1, reset.Versions);
        Assert.False(_host.Server.Has(Carry));
        Assert.Equal([$"delete {Carry}"], _host.Server.Writes);
        Assert.Equal(CarryText, File.ReadAllText(ModConfigBackups.ForProfile(_host.Profile).Versions("carryon.json").Single()));

        var back = await source.UndoAsync("carryon.json");

        Assert.True(back.Exists);
        Assert.Equal(CarryText, _host.Server.Text(Carry));
        Assert.Equal(0, back.Versions);

        // файла нет — сбрасывать нечего
        _host.Server.Writes.Clear();
        File.Delete(Path.Combine(_host.Server.Root, "ModConfig", "carryon.json"));
        Assert.False((await source.ResetAsync("carryon.json")).Exists);
        Assert.Empty(_host.Server.Writes);
    }

    // ---------- пути

    [Theory]
    [InlineData("../serverconfig.json")]
    [InlineData("..\\serverconfig.json")]
    [InlineData("AutoMapMarkers/../../serverconfig.json")]
    [InlineData("/serverconfig.json")]
    [InlineData("\\serverconfig.json")]
    [InlineData("C:/serverconfig.json")]
    [InlineData("icon.png")]
    [InlineData("AutoMapMarkers")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("a\nb.json")]
    public async Task PathOutsideModConfig_OrNotAConfig_IsRefused(string path)
    {
        var source = await OpenedAsync();
        var before = _host.Server.Text("/serverconfig.json");

        foreach (var call in new Func<Task>[]
                 {
                     () => source.ReadAsync(path),
                     () => source.SaveAsync(new ModConfigSaveRequest(path, "{}", null)),
                     () => source.UndoAsync(path),
                     () => source.ResetAsync(path),
                 })
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(call);
            AssertText("mcfg.badPath", ex.Message, path);
        }

        Assert.Empty(_host.Server.Writes);
        Assert.Equal(before, _host.Server.Text("/serverconfig.json"));
    }

    [Fact]
    public void OnlyForAHostedTarget()
    {
        var own = new GameProfile { Id = "own-" + Guid.NewGuid().ToString("N"), Name = "own", Kind = ProfileKind.Server, DataDir = _host.Dir };
        Assert.Throws<ArgumentException>(() => new HostedModConfigSource(ModTarget.For(own)!));
    }

    // ---------- сервер, который переименовывает поверх и отдаёт LIST

    [Fact]
    public async Task ServerThatRenamesOver_WritesInPlace_WithoutBak_AndComparesListTimes()
    {
        using var host = new HostedModConfigsTests(gportal: false);
        host._host.Server.ListLocalTime = true; // LIST: местное время сервера до минуты
        var source = await host.OpenedAsync();
        var opened = await source.ReadAsync("carryon.json");
        Assert.Equal(DateTimeKind.Unspecified, opened.ChangedUtc!.Value.Kind);

        var result = await source.SaveAsync(new ModConfigSaveRequest("carryon.json", "{ \"Range\": 7 }", opened.ChangedUtc));

        Assert.False(result.Changed);
        Assert.Equal("{ \"Range\": 7 }", host._host.Server.Text(Carry));
        Assert.Equal([$"upload {Tmp}", $"rename {Tmp} → {Carry}"], host._host.Server.Writes);

        File.SetLastWriteTimeUtc(Path.Combine(host._host.Server.Root, "ModConfig", "carryon.json"), DateTime.UtcNow.AddHours(1));
        Assert.True((await source.SaveAsync(new ModConfigSaveRequest("carryon.json", "{}", result.Content!.ChangedUtc))).Changed);
    }
}

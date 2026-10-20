using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using eViSTool.Core;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Versioning;
using eViSTool.Presentation.ViewModels;
using eViSTool.Presentation.ViewModels.Mods;
using Newtonsoft.Json;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Установка в цель для «Моих модов»: архив встаёт в папку цели, вопрос про ту же версию задаётся только когда
/// просили, скачанный релиз ставится, ошибка скачивания — строкой отчёта; подписи отчёта по профилям. Цель —
/// серверный профиль во временной папке (данные пользователя не трогаются), модбаза — поддельный ответ без сети.
/// </summary>
public sealed class ModInstallServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-install-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _id = "install-" + Guid.NewGuid().ToString("N")[..8];
    private readonly List<string> _statuses = [];

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        // прежние версии и история — в папке данных тестового прогона, под id профиля
        var backups = Path.Combine(AppPaths.ModBackups, _id);
        if (Directory.Exists(backups)) Directory.Delete(backups, recursive: true);
        if (File.Exists(ModHistory.FileFor(_id))) File.Delete(ModHistory.FileFor(_id));
        if (File.Exists(ReleaseMarks.FileFor(_id))) File.Delete(ReleaseMarks.FileFor(_id));
    }

    /// <summary>Главная модель без настроек: копии миров выключены, открытого профиля нет.</summary>
    private ModInstallService Service(HttpMessageHandler? modDb = null) => new(
        (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel)),
        new ModUpdater(new ModDbClient(new HttpClient(modDb ?? new FakeModDb(_ => throw new HttpRequestException("сети нет"))))),
        _statuses.Add, () => null);

    private ModTarget Server()
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, "server")).FullName;
        var mods = Directory.CreateDirectory(Path.Combine(data, "Mods")).FullName;
        File.WriteAllText(Path.Combine(data, "serverconfig.json"), JsonConvert.SerializeObject(new { ModPaths = new[] { "Mods", mods } }));
        return ModTarget.For(new GameProfile { Id = _id, Name = "Сервер", Kind = ProfileKind.Server, DataDir = data })!;
    }

    private string ModsDir => Path.Combine(_root, "server", "Mods");

    private string Zip(string fileName, string modId, string version)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "dl")).FullName;
        var path = Path.Combine(dir, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open());
        w.Write($$"""{ "modid": "{{modId}}", "name": "{{modId}}", "version": "{{version}}" }""");
        return path;
    }

    /// <summary>Модбаза в тесте: каждый запрос — в функцию теста, наружу ничего не уходит.</summary>
    private sealed class FakeModDb(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    // ---- подписи отчёта

    [Fact]
    public async Task Labelled_SeveralProfiles_SignsOnlyLinesOfThisStep()
    {
        var target = new ModTarget(new GameProfile { Id = "c", Name = "Client C" }, null);
        var installed = new List<string> { "Прежний 1.0" };
        var problems = new List<string> { "прежняя беда" };

        var ok = await ModInstallService.Labelled(target, label: true, installed, problems, () =>
        {
            installed.Add("Carry On 1.14");
            problems.Add("нет сети");
            return Task.FromResult(true);
        });

        Assert.True(ok);
        Assert.Equal(["Прежний 1.0", "Carry On 1.14 → Client C"], installed);
        Assert.Equal(["прежняя беда", "Client C: нет сети"], problems);
    }

    [Fact]
    public async Task Labelled_OneProfile_LinesAsIs()
    {
        var target = new ModTarget(new GameProfile { Id = "c", Name = "Client C" }, null);
        var installed = new List<string>();
        var problems = new List<string>();

        var ok = await ModInstallService.Labelled(target, label: false, installed, problems, () =>
        {
            problems.Add("нет сети");
            return Task.FromResult(false);
        });

        Assert.False(ok);
        Assert.Equal(["нет сети"], problems);
    }

    [Fact]
    public void WithWorldCopies_NoneCopied_TextAsIs() =>
        Assert.Equal("Мод выключен", Service().WithWorldCopies("Мод выключен"));

    // ---- архив

    [Fact]
    public void Zip_NewMod_LandsInTarget() => Sta.Run(() =>
    {
        var installed = new List<string>();
        var problems = new List<string>();

        var task = Service().InstallZipAsync(Server(), Zip("alpha.zip", "alpha", "1.0.0"), interactive: true, installed, problems);
        Sta.Wait(task);

        Assert.True(task.Result);
        Assert.Equal(["alpha 1.0.0"], installed);
        Assert.Empty(problems);
        Assert.Equal(["alpha.zip"], Directory.GetFiles(ModsDir).Select(Path.GetFileName));
    });

    // «Добавить» тот же файл ещё раз — спросить; отказались — ничего не поменялось и в отчёте пусто
    [Fact]
    public void Zip_SameVersion_Interactive_AskedAndDeclined() => Sta.Run(() =>
    {
        var service = Service();
        var target = Server();
        Sta.Wait(service.InstallZipAsync(target, Zip("alpha.zip", "alpha", "1.0.0"), interactive: false, [], []));
        using var dialogs = new ScriptedDialogs(Answer.No);
        var installed = new List<string>();
        var problems = new List<string>();

        var again = service.InstallZipAsync(target, Zip("alpha-again.zip", "alpha", "1.0.0"), interactive: true, installed, problems);
        Sta.Wait(again);

        Assert.False(again.Result);
        Assert.Equal([Loc.T("mods.reinstallConfirm", "alpha", "1.0.0")], dialogs.Asked);
        Assert.Empty(installed);
        Assert.Empty(problems);
        Assert.Equal(["alpha.zip"], Directory.GetFiles(ModsDir).Select(Path.GetFileName));
    });

    // обновление и откат решены заранее — та же версия ставится без вопроса
    [Fact]
    public void Zip_SameVersion_NotInteractive_NoQuestion() => Sta.Run(() =>
    {
        var service = Service();
        var target = Server();
        Sta.Wait(service.InstallZipAsync(target, Zip("alpha.zip", "alpha", "1.0.0"), interactive: false, [], []));
        using var dialogs = new ScriptedDialogs(); // любой вопрос уронит тест
        var installed = new List<string>();

        var again = service.InstallZipAsync(target, Zip("alpha-again.zip", "alpha", "1.0.0"), interactive: false, installed, []);
        Sta.Wait(again);

        Assert.True(again.Result);
        Assert.Empty(dialogs.Asked);
        Assert.Single(installed);
    });

    // ---- релиз модбазы

    [Fact]
    public void Download_Release_InstalledIntoTarget() => Sta.Run(() =>
    {
        var bytes = File.ReadAllBytes(Zip("beta_2.0.0.zip", "beta", "2.0.0"));
        var service = Service(new FakeModDb(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var release = new ModDbRelease { ModVersion = "2.0.0", MainFile = "https://mods.vintagestory.at/files/beta_2.0.0.zip", FileName = "beta_2.0.0.zip" };
        var installed = new List<string>();
        var problems = new List<string>();

        var task = service.DownloadAndInstallAsync(Server(), "beta", "Beta", release, installed, problems);
        Sta.Wait(task);

        Assert.True(task.Result);
        Assert.Equal(["beta 2.0.0"], installed);
        Assert.Empty(problems);
        Assert.Equal(["beta_2.0.0.zip"], Directory.GetFiles(ModsDir).Select(Path.GetFileName));
    });

    // версия в файле не та, что на ModDB: мод ставится, предупреждение — в итог, повторная проверка тот же релиз не предлагает
    [Fact]
    public void Download_VersionMismatch_InstalledWithNote_NotOfferedAgain() => Sta.Run(() =>
    {
        var bytes = File.ReadAllBytes(Zip("beta_2.0.0.zip", "beta", "1.9.0"));
        var service = Service(new FakeModDb(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var release = new ModDbRelease
        {
            ReleaseId = 5, ModVersion = "2.0.0", MainFile = "https://mods.vintagestory.at/files/beta_2.0.0.zip", FileName = "beta_2.0.0.zip",
            Tags = ["1.21.0"],
        };
        var target = Server();
        var installed = new List<string>();
        var problems = new List<string>();
        var notes = new List<string>();

        var task = service.DownloadAndInstallAsync(target, "beta", "Beta", release, installed, problems, notes: notes);
        Sta.Wait(task);

        Assert.True(task.Result);
        Assert.Equal(["beta 1.9.0"], installed);
        Assert.Empty(problems);
        Assert.Equal([Loc.T("dl.versionMismatch", "Beta", "1.9.0", "2.0.0")], notes);

        var scan = ModTargets.ScanAsync(target);
        Sta.Wait(scan);
        var remote = new Dictionary<string, ModDbResult> { ["beta"] = new(new ModDbMod { Releases = [release] }, null) };
        var check = UpdateChecker.Evaluate(scan.Result.Mods, remote, ModVersion.ParseOrNull("1.21.6")!).Single();
        Assert.Equal(ModStatus.UpToDate, check.Status);
    });

    /// <summary>
    /// Автор сменил modid в новой версии (GiMap 3.0.0: gimap → gimapcore, у релиза на модбазе modidstr — новый): архив не
    /// «чужой», мод ставится, а прежний с тем же назначением убирается — иначе игра загрузила бы оба.
    /// </summary>
    [Fact]
    public void Download_AuthorRenamedModId_InstalledAndOldOneRemoved() => Sta.Run(() =>
    {
        var target = Server();
        File.Copy(Zip("gimap_2.1.0.zip", "gimap", "2.1.0"), Path.Combine(ModsDir, "gimap_2.1.0.zip"));
        var bytes = File.ReadAllBytes(Zip("gimapcore_3.0.0.zip", "gimapcore", "3.0.0"));
        var service = Service(new FakeModDb(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var release = new ModDbRelease
        {
            ModIdStr = "gimapcore", ModVersion = "3.0.0", MainFile = "https://mods.vintagestory.at/files/gimapcore_3.0.0.zip",
            FileName = "gimapcore_3.0.0.zip",
        };
        var installed = new List<string>();
        var problems = new List<string>();
        var notes = new List<string>();

        var task = service.DownloadAndInstallAsync(target, "gimap", "GiMap", release, installed, problems, notes: notes);
        Sta.Wait(task);

        Assert.True(task.Result);
        Assert.Empty(problems);
        Assert.Equal(["gimapcore_3.0.0.zip"], Directory.GetFiles(ModsDir).Select(Path.GetFileName));
        Assert.Equal([Loc.T("dl.renamed", "GiMap", "gimap", "gimapcore")], notes);
    });

    /// <summary>В архиве мод, которого модбаза у релиза не обещала (ни modid мода, ни modidstr релиза), — не ставится.</summary>
    [Fact]
    public void Download_ArchiveWithAnotherMod_Rejected() => Sta.Run(() =>
    {
        var bytes = File.ReadAllBytes(Zip("gimapcore_3.0.0.zip", "somethingelse", "3.0.0"));
        var service = Service(new FakeModDb(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var release = new ModDbRelease
        {
            ModIdStr = "gimapcore", ModVersion = "3.0.0", MainFile = "https://mods.vintagestory.at/files/gimapcore_3.0.0.zip",
            FileName = "gimapcore_3.0.0.zip",
        };
        var problems = new List<string>();

        var task = service.DownloadAndInstallAsync(Server(), "gimap", "GiMap", release, [], problems);
        Sta.Wait(task);

        Assert.False(task.Result);
        Assert.Equal([Loc.T("dl.failed", "GiMap", "3.0.0", Loc.T("dl.wrongMod", "somethingelse", "gimap"))], problems);
        Assert.Empty(Directory.GetFiles(ModsDir));
    });

    [Fact]
    public void Download_SameVersion_NoNote() => Sta.Run(() =>
    {
        var bytes = File.ReadAllBytes(Zip("beta_2.0.0.zip", "beta", "v2.0.0"));
        var service = Service(new FakeModDb(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var release = new ModDbRelease { ModVersion = "2.0.0", MainFile = "https://mods.vintagestory.at/files/beta_2.0.0.zip", FileName = "beta_2.0.0.zip" };
        var notes = new List<string>();

        var task = service.DownloadAndInstallAsync(Server(), "beta", "Beta", release, [], [], notes: notes);
        Sta.Wait(task);

        Assert.True(task.Result);
        Assert.Empty(notes);
    });

    [Fact]
    public void WithNotes_AppendsOnlyWhenThereAreNotes()
    {
        Assert.Equal("Готово", ModInstallService.WithNotes("Готово", null));
        Assert.Equal("Готово", ModInstallService.WithNotes("Готово", ["", ""]));
        Assert.Equal("Готово · А · Б", ModInstallService.WithNotes("Готово", ["А", "Б", "А"]));
        Assert.Equal("А", ModInstallService.WithNotes("", ["А"]));
    }

    [Fact]
    public void Download_Fails_ProblemNamesTheRelease() => Sta.Run(() =>
    {
        var release = new ModDbRelease { ModVersion = "2.0.0", MainFile = "https://mods.vintagestory.at/files/beta_2.0.0.zip" };
        var installed = new List<string>();
        var problems = new List<string>();

        var task = Service().DownloadAndInstallAsync(Server(), "beta", "Beta", release, installed, problems);
        Sta.Wait(task);

        Assert.False(task.Result);
        Assert.Empty(installed);
        Assert.Equal([Loc.T("dl.failed", "Beta", "2.0.0", "сети нет")], problems);
        Assert.Empty(Directory.GetFiles(ModsDir));
    });
}

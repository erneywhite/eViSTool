using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;
using Newtonsoft.Json;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// «Мои моды»: смена профиля, пока идёт проверка обновлений прежнего. Модбаза поддельная — проверка первого профиля
/// висит на ней, пока её не отменят; оба профиля свои, с поддельной игрой и модом во временной папке. Главная модель
/// настоящая: ей принадлежат активный профиль и настройка автопроверки. Данные пользователя не трогаются.
/// </summary>
public sealed class ModsCheckProfileSwitchTests : IDisposable
{
    private static readonly string FakeBin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "tests", "FakeVsServer", "bin", "Debug", "net10.0"));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-modcheck-" + Guid.NewGuid().ToString("N"));
    private readonly IAppWindows? _oldWindows = AppWindows.IsSet ? AppWindows.Current : null;
    private readonly string _game;

    public ModsCheckProfileSwitchTests()
    {
        // «папка игры» — поддельный сервер под именем настоящего: версия игры (1.22.7) берётся из его файла
        _game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        File.Copy(Path.Combine(FakeBin, "FakeVsServer.dll"), Path.Combine(_game, "VintagestoryServer.dll"));
    }

    public void Dispose()
    {
        AppWindows.Current = _oldWindows!;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Модбаза: «alpha» отвечает, только когда запрос отменят; остальных модов в ней нет, каталог пуст.</summary>
    private sealed class FakeModDb : HttpMessageHandler
    {
        public ConcurrentQueue<string> Asked { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/api/mod/", StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(path["/api/mod/".Length..]);
                Asked.Enqueue(id);
                if (id == "alpha") await Task.Delay(Timeout.Infinite, ct);
                return Json(new { statuscode = "404" });
            }
            if (path == "/api/mods") return Json(new { statuscode = "200", mods = Array.Empty<object>() });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object o) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(o), Encoding.UTF8, "application/json") };
    }

    private static byte[] Zip(string modId)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open()))
            w.Write($"{{ \"type\": \"code\", \"modid\": \"{modId}\", \"name\": \"{modId}\", \"version\": \"1.0.0\" }}");
        return ms.ToArray();
    }

    /// <summary>Свой клиентский профиль с одним модом в папке данных.</summary>
    private ProfileViewModel Profile(string name, string modId)
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(data, "Mods")).FullName, modId + "_1.0.0.zip"), Zip(modId));
        var model = new GameProfile
        {
            Id = name + "-" + Guid.NewGuid().ToString("N")[..6], Name = name, Kind = ProfileKind.Client, GameDir = _game, DataDir = data,
        };
        return new ProfileViewModel(model, _ => { });
    }

    /// <summary>
    /// Главная модель с поддельной модбазой и профилями A (мод alpha) и B (мод beta); A открыт. Автопроверка включена
    /// или выключена на время теста (настройка пишется в settings.json тестов — после теста она возвращается).
    /// </summary>
    private void WithTwoProfiles(bool autoCheck, Action<MainViewModel, ProfileViewModel, FakeModDb> body)
    {
        Assert.NotNull(GameInstall.DetectVersion(_game));
        var db = new FakeModDb();
        var language = Loc.Instance.Language; // главная модель выставляет язык из настроек — другим тестам его не менять
        var main = new MainViewModel(new ModDbClient(new HttpClient(db) { BaseAddress = new Uri(ModDbClient.ApiBase) }));
        Loc.Instance.SetLanguage(language);
        var saved = main.AutoCheckUpdates;
        try
        {
            main.AutoCheckUpdates = autoCheck;
            main.Profiles.Clear(); // профили из настроек тесту не нужны
            var (a, b) = (Profile("a", "alpha"), Profile("b", "beta"));
            main.Profiles.Add(a);
            main.Profiles.Add(b);
            main.ActiveProfile = a;
            Sta.WaitFor(() => main.Mods.Rows.Any(r => r.ModId == "alpha"));
            body(main, b, db);
        }
        finally
        {
            main.AutoCheckUpdates = saved;
        }
    }

    [Fact]
    public void CheckCancelledByAProfileSwitch_DoesNotOverwriteTheNewProfilesStatus() => Sta.Run(() =>
        WithTwoProfiles(autoCheck: false, (main, b, db) =>
        {
            var mods = main.Mods;
            mods.CheckCommand.Execute(null);
            Sta.WaitFor(() => db.Asked.Contains("alpha")); // проверка A дошла до модбазы и висит

            main.ActiveProfile = b;
            Sta.WaitFor(() => !mods.CheckCommand.IsRunning && mods.Rows.Any(r => r.ModId == "beta"));

            // «проверка отменена» — про A; у B своя строка состояния, и автопроверка выключена
            Assert.Equal(Loc.T("mods.readFromDisk"), mods.StatusText);
            Assert.DoesNotContain("beta", db.Asked);
        }));

    [Fact]
    public void ProfileSwitchedWhileTheOldOneIsBeingChecked_TheNewOneIsCheckedAfterIt() => Sta.Run(() =>
        WithTwoProfiles(autoCheck: true, (main, b, db) =>
        {
            var mods = main.Mods;
            Sta.WaitFor(() => db.Asked.Contains("alpha")); // автопроверка A дошла до модбазы и висит
            Assert.True(mods.CheckCommand.IsRunning);

            // смена отменяет проверку A, но та заканчивается позже: раньше автопроверка B в этот миг молча пропускалась
            main.ActiveProfile = b;
            Sta.WaitFor(() => mods.ModDbAnswers.ContainsKey("beta") && !mods.CheckCommand.IsRunning);

            Assert.False(mods.ModDbAnswers.ContainsKey("alpha"));
            Assert.Single(db.Asked, id => id == "beta");
            Assert.NotEqual(Loc.T("mods.checkCancelled"), mods.StatusText);
        }));
}

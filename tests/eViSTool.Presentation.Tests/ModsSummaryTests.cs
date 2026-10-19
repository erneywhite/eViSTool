using System.IO.Compression;
using System.Net;
using System.Text;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Profiles;
using eViSTool.Core.Tests.Hosting;
using eViSTool.Presentation.ViewModels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// «Мои моды», правая панель без выбранного мода — сводка профиля: сколько модов включено из всех, сколько с проблемами,
/// когда проверялись обновления; пока идёт проверка — IsChecking (по нему таблица показывает заглушки). Числа — те же,
/// что на вкладках-счётчиках, у своего профиля и у сервера у хостера. Снять выбор (Esc и «Назад» мыши) — снова сводка;
/// так же закрывается карточка в «Каталоге». Модбаза поддельная (ответ задерживается, пока тест не отпустит), профиль —
/// во временной папке; данные пользователя не трогаются.
/// </summary>
public sealed class ModsSummaryTests : IDisposable
{
    private static readonly string FakeBin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "tests", "FakeVsServer", "bin", "Debug", "net10.0"));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-modsummary-" + Guid.NewGuid().ToString("N"));
    private readonly IAppWindows? _oldWindows = AppWindows.IsSet ? AppWindows.Current : null;
    private (MainViewModel Main, bool AutoCheck)? _main;

    public void Dispose()
    {
        // настройка автопроверки пишется в settings.json тестов — вернуть, как была
        if (_main is var (main, autoCheck)) main.AutoCheckUpdates = autoCheck;
        AppWindows.Current = _oldWindows!;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Модбаза: ни одного мода в ней нет; закрыт <see cref="Gate"/> — ответ ждёт, пока его не откроют.</summary>
    private sealed class FakeModDb : HttpMessageHandler
    {
        public FakeModDb(bool open = true)
        {
            if (open) Gate.SetResult();
        }

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Asked;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Asked);
            await Gate.Task.WaitAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/api/mod/", StringComparison.Ordinal)) return Json(new { statuscode = "404" });
            if (path == "/api/mods") return Json(new { statuscode = "200", mods = Array.Empty<object>() });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object o) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(o), Encoding.UTF8, "application/json") };
    }

    /// <summary>Архив мода; <paramref name="needs"/> — modid зависимости, которой в профиле нет (мод с проблемой).</summary>
    private static byte[] Zip(string modId, string? needs = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open()))
        {
            var deps = needs is null ? "" : $", \"dependencies\": {{ \"{needs}\": \"1.0.0\" }}";
            w.Write($"{{ \"type\": \"code\", \"modid\": \"{modId}\", \"name\": \"{modId}\", \"version\": \"1.0.0\"{deps} }}");
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Свой клиентский профиль с игрой 1.22.7 и тремя модами: alpha, beta (выключен в настройках игры) и gamma (ждёт
    /// мод missing, которого нет).
    /// </summary>
    private ProfileViewModel ClientProfile(string name)
    {
        var game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        if (!File.Exists(Path.Combine(game, "VintagestoryServer.dll")))
            File.Copy(Path.Combine(FakeBin, "FakeVsServer.dll"), Path.Combine(game, "VintagestoryServer.dll"));
        var data = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var mods = Directory.CreateDirectory(Path.Combine(data, "Mods")).FullName;
        File.WriteAllBytes(Path.Combine(mods, "alpha_1.0.0.zip"), Zip("alpha"));
        File.WriteAllBytes(Path.Combine(mods, "beta_1.0.0.zip"), Zip("beta"));
        File.WriteAllBytes(Path.Combine(mods, "gamma_1.0.0.zip"), Zip("gamma", needs: "missing"));
        File.WriteAllText(Path.Combine(data, "clientsettings.json"), """{ "stringListSettings": { "disabledMods": ["beta"] } }""");
        var model = new GameProfile
        {
            Id = name + "-" + Guid.NewGuid().ToString("N")[..6], Name = name, Kind = ProfileKind.Client, GameDir = game, DataDir = data,
        };
        return new ProfileViewModel(model, _ => { });
    }

    /// <summary>Главная модель с поддельной модбазой и данными профилями, без автопроверки; открыт первый.</summary>
    private MainViewModel Main(FakeModDb db, params ProfileViewModel[] profiles)
    {
        var language = Loc.Instance.Language; // главная модель выставляет язык из настроек — другим тестам его не менять
        var main = new MainViewModel(new ModDbClient(new HttpClient(db) { BaseAddress = new Uri(ModDbClient.ApiBase) }));
        Loc.Instance.SetLanguage(language);
        _main = (main, main.AutoCheckUpdates);
        main.AutoCheckUpdates = false;
        main.Profiles.Clear(); // профили пользователя из настроек тесту не нужны
        foreach (var p in profiles) main.Profiles.Add(p);
        main.ActiveProfile = profiles[0];
        return main;
    }

    private static void AssertSummaryMatchesCounters(ModsViewModel mods)
    {
        Assert.Equal(Loc.T("mods.summaryOf", mods.ModCount - mods.DisabledCount, mods.ModCount), mods.SummaryEnabledText);
        Assert.Equal(mods.ProblemCount == 0 ? Loc.T("mods.summaryNone") : mods.ProblemCount.ToString(Loc.Culture), mods.SummaryProblemsText);
        Assert.Equal(mods.ProblemCount > 0, mods.HasProblems);
    }

    [Fact]
    public void Summary_CountsEnabledModsAndProblems_LikeTheTabCounters() => Sta.Run(() =>
    {
        var db = new FakeModDb();
        var profile = ClientProfile("a");
        Assert.NotNull(GameInstall.DetectVersion(profile.Model.GameDir!));
        var mods = Main(db, profile).Mods;
        Sta.WaitFor(() => mods.ModCount == 3);

        Assert.Equal($"a · {profile.KindText}", mods.SummaryTitle);
        Assert.Equal(1, mods.DisabledCount);
        Assert.Equal(1, mods.ProblemCount); // gamma ждёт missing
        Assert.Equal(Loc.T("mods.summaryOf", 2, 3), mods.SummaryEnabledText);
        Assert.Equal("1", mods.SummaryProblemsText);
        Assert.True(mods.HasProblems);
        Assert.Equal(Loc.T("mods.summaryNotChecked"), mods.SummaryCheckedText);
        Assert.Null(mods.CheckedAt);
        Assert.False(mods.IsChecking);
        Assert.False(mods.HasUpdates);
        Assert.Null(mods.Card); // мод не выбран — справа сводка
        AssertSummaryMatchesCounters(mods);

        // выбрали мод — справа его карточка (сводка видна, только пока карточки нет)
        mods.Selected = mods.Rows.First(r => r.ModId == "alpha");
        Assert.NotNull(mods.Card);
    });

    [Fact]
    public void Check_IsSeenWhileItRuns_AndTheSummarySaysWhenItWasDone_UntilAProfileSwitch() => Sta.Run(() =>
    {
        var db = new FakeModDb(open: false);
        var (a, b) = (ClientProfile("a"), ClientProfile("b"));
        var main = Main(db, a, b);
        var mods = main.Mods;
        Sta.WaitFor(() => mods.ModCount == 3 && !mods.CheckCommand.IsRunning);
        var changes = new List<string?>();
        mods.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        var asked = db.Asked;

        mods.CheckCommand.Execute(null);
        Sta.WaitFor(() => db.Asked > asked); // проверка дошла до модбазы и ждёт её ответа
        Assert.True(mods.IsChecking);
        Assert.Contains(nameof(ModsViewModel.IsChecking), changes);
        Assert.Null(mods.CheckedAt);

        db.Gate.SetResult();
        Sta.WaitFor(() => !mods.CheckCommand.IsRunning);
        Assert.False(mods.IsChecking);
        Assert.Equal(2, changes.Count(n => n == nameof(ModsViewModel.IsChecking))); // начало и конец
        var at = Assert.NotNull(mods.CheckedAt);
        Assert.InRange(DateTime.Now - at, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        Assert.Equal(Loc.T("mods.summaryCheckedAt", Dates.Relative(at)), mods.SummaryCheckedText);
        Assert.Contains(nameof(ModsViewModel.SummaryCheckedText), changes);
        AssertSummaryMatchesCounters(mods);

        // другой профиль: ответ модбазы был про прежний — обновления снова «ещё не проверялись»
        main.ActiveProfile = b;
        Sta.WaitFor(() => mods.ModCount == 3 && mods.Rows.All(r => r.FilePath.StartsWith(b.Model.DataDir!, StringComparison.Ordinal)));
        Assert.Null(mods.CheckedAt);
        Assert.Equal(Loc.T("mods.summaryNotChecked"), mods.SummaryCheckedText);
        Assert.Equal($"b · {b.KindText}", mods.SummaryTitle);
    });

    [Fact]
    public void HostedServer_SummaryUsesTheSameCountsAsTheTabs() => Sta.Run(() =>
    {
        using var host = new HostedFixture();
        host.GPortalLayout();
        host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        host.PutMod("/Mods/beta_1.0.0.zip", "beta", "1.0.0");
        host.Server.Put("/Mods/gamma_1.0.0.zip", Zip("gamma", needs: "missing"));
        var config = JObject.Parse(host.Server.Text("/serverconfig.json"));
        ((JObject)(config["WorldConfig"] ??= new JObject()))["DisabledMods"] = new JArray("beta");
        host.Server.Put("/serverconfig.json", config.ToString());

        var profile = new ProfileViewModel(host.Profile, _ => { });
        var mods = Main(new FakeModDb(), profile).Mods;
        Sta.WaitFor(() => mods.HostedSource.Resolved is not null && !mods.HostedSource.IsLoading);

        // alpha, beta, gamma и мод самого хостера: он включён и не проблема
        Assert.Equal(4, mods.ModCount);
        Assert.Equal(1, mods.DisabledCount);
        Assert.Equal(1, mods.ProblemCount);
        Assert.Equal(Loc.T("mods.summaryOf", 3, 4), mods.SummaryEnabledText);
        Assert.Equal($"Hosted · {profile.KindText}", mods.SummaryTitle);
        AssertSummaryMatchesCounters(mods);
    });

    [Fact]
    public void ClearSelection_ReturnsTheRightPanelToTheSummary_ButNotInHistory() => Sta.Run(() =>
    {
        var mods = Main(new FakeModDb(), ClientProfile("a")).Mods;
        Sta.WaitFor(() => mods.ModCount == 3);
        var command = mods.ClearSelectionCommand;
        Assert.False(command.CanExecute(null)); // мод не выбран — снимать нечего, Esc идёт дальше
        var changed = 0;
        command.CanExecuteChanged += (_, _) => changed++;

        mods.Selected = mods.Rows.First(r => r.ModId == "alpha");
        Assert.NotNull(mods.Card);
        Assert.True(command.CanExecute(null));
        Assert.NotEqual(0, changed);

        command.Execute(null);
        Assert.Null(mods.Selected);
        Assert.Null(mods.Card); // справа снова сводка
        Assert.False(command.CanExecute(null));

        // таблицу перестроили (моды перечитаны) — выбор сам не вернулся
        Sta.Wait(mods.ReloadAsync());
        Assert.Null(mods.Selected);
        Assert.Null(mods.Card);

        // «История»: справа сеанс, а не мод — снимать нечего, даже если в спрятанной таблице мод выбран
        mods.Selected = mods.Rows.First(r => r.ModId == "beta");
        mods.Filter = ModFilter.History;
        Assert.False(command.CanExecute(null));
        mods.Filter = ModFilter.All;
        Assert.True(command.CanExecute(null));
    });

    [Fact]
    public void CatalogCard_ClosesOnClearSelection_LikeTheModsCard() => Sta.Run(() =>
    {
        var catalog = Main(new FakeModDb(), ClientProfile("a")).Catalog;
        var command = catalog.ClearSelectionCommand;
        Assert.False(command.CanExecute(null));

        catalog.Selected = new CatalogItemViewModel(new ModDbListItem { ModId = 1, AssetId = 1, Name = "Alpha" }, new Dictionary<string, string>());
        Assert.NotNull(catalog.Details);
        Assert.True(command.CanExecute(null));

        command.Execute(null);
        Assert.Null(catalog.Selected);
        Assert.Null(catalog.Details); // справа снова «Выбери мод»
        Assert.False(command.CanExecute(null));
    });

    [Fact]
    public void SummaryTexts_FollowTheLanguage() => Sta.Run(() =>
    {
        var language = Loc.Instance.Language;
        var db = new FakeModDb();
        var mods = Main(db, ClientProfile("a")).Mods;
        Sta.WaitFor(() => mods.ModCount == 3);
        var changes = new List<string?>();
        mods.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        try
        {
            Loc.Instance.SetLanguage(language == "ru" ? "en" : "ru");
            mods.OnLanguageChanged();
            Assert.Equal(Loc.T("mods.summaryOf", 2, 3), mods.SummaryEnabledText);
            foreach (var name in new[] { nameof(ModsViewModel.SummaryTitle), nameof(ModsViewModel.SummaryEnabledText),
                         nameof(ModsViewModel.SummaryProblemsText), nameof(ModsViewModel.SummaryCheckedText) })
                Assert.Contains(name, changes);
        }
        finally
        {
            Loc.Instance.SetLanguage(language);
        }
    });
}

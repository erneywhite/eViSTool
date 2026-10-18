using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Tests;
using eViSTool.Core.Tests.Hosting;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Копия мира сервера у хостера держит соединение профиля всю передачу (поддельный FTP «как G-Portal», скачивание мира
/// придержано на первом байте). «Обзор» раздела «Сервер» и «Мои моды» в это время в сеть не ходят, а говорят, что идёт и
/// где отменить; команды «Моих модов», которые пошли бы на сервер, отвечают тем же сразу — без вопросов и запросов.
/// Копия кончилась — строка пропадает, «Обзор» читает лог, «Мои моды» дочитывают отложенное.
/// </summary>
public sealed class HostedWorldBusyTests : IDisposable
{
    private const string Main = "/Logs/server-main.log";
    private const string World = "/Saves/MyVintageStoryWorld.vcdbs";

    private readonly HostedFixture _host = new();
    private readonly ManualResetEventSlim _reached = new();
    private readonly ManualResetEventSlim _release = new();

    public HostedWorldBusyTests()
    {
        _host.GPortalLayout(log: false);
        _host.Settings.BackupDir = Path.Combine(_host.Dir, "copies"); // копии — во временную папку стенда
        PutLog(HostedLogSamples.Stopped);
        PutWorld("one", "two");
        // скачивание мира стоит, пока тест его не отпустит: копия держит соединение
        _host.Server.OnBefore = (op, path) =>
        {
            if (op != "download" || path != World) return;
            _reached.Set();
            _release.Wait(TimeSpan.FromSeconds(20));
        };
    }

    public void Dispose()
    {
        _release.Set();
        _host.Dispose();
        _reached.Dispose();
        _release.Dispose();
    }

    // ---------- стенд

    private void PutLog(string[] lines)
    {
        var marks = new HostedRunMarks();
        foreach (var line in lines) marks.Add(line);
        var local = _host.Server.Put(Main, HostedLogSamples.Bytes(lines));
        File.SetLastWriteTimeUtc(local, DateTime.SpecifyKind(marks.LastTime!.Value.AddHours(-2), DateTimeKind.Utc));
    }

    private void PutWorld(params string[] rows)
    {
        var local = Path.Combine([_host.Server.Root, .. World.Split('/', StringSplitOptions.RemoveEmptyEntries)]);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        SqliteWorld.WriteClean(local, rows);
    }

    /// <summary>Сколько всего запросов получил «сервер».</summary>
    private int Calls()
    {
        lock (_host.Server.Calls) return _host.Server.Calls.Values.Sum();
    }

    /// <summary>Начать копию и дождаться, пока она встанет на скачивании мира (соединение занято).</summary>
    private HostedWorldJob StartCopy(HostedWorldJobs jobs)
    {
        var job = jobs.StartBackup(_host.Target, scheduled: false);
        Assert.NotNull(job);
        Sta.WaitFor(() => _reached.IsSet);
        return job;
    }

    private void FinishCopy(HostedWorldJob job)
    {
        _release.Set();
        Sta.WaitFor(() => job.IsFinished);
        UiThread.Current!.RunPending(); // итог приходит в поток окна
    }

    // ---------- «Обзор»

    [Fact]
    public void Overview_DuringAWorldCopy_DoesNotPoll_SaysWhy_InTheCurrentLanguage_AndReadsTheLogAfterIt() => Sta.Run(() =>
    {
        var jobs = new HostedWorldJobs();
        var vm = new HostedServerViewModel(null!) { Jobs = jobs, CrashesSince = DateTime.Now };
        vm.OnProfileSwitched(_host.Profile);
        Assert.Equal("", vm.WorldJobText);
        var language = Loc.Instance.Language;
        try
        {
            var job = StartCopy(jobs);
            Assert.Equal(HostedWorldBusy.Describe(job), vm.WorldJobText);
            Assert.Contains(HostedWorldText.Progress(job), vm.WorldJobText); // ход — тот же, что во вкладке «Копии»

            var calls = Calls();
            vm.SetActive(true);      // раздел открыли посреди копии
            Sta.Wait(vm.PollAsync()); // и опрос по таймеру — тоже
            Assert.Equal(calls, Calls()); // в занятое соединение не стучались: опрос не висит в его очереди
            Assert.Empty(vm.Lines);
            Assert.Equal(Loc.T("server.hostedReadingLog"), vm.StateNote);

            var before = vm.WorldJobText;
            Loc.Instance.SetLanguage(language == "ru" ? "en" : "ru");
            vm.OnLanguageChanged();
            Assert.Equal(HostedWorldBusy.Describe(job), vm.WorldJobText);
            Assert.NotEqual(before, vm.WorldJobText);
            Loc.Instance.SetLanguage(language);
            vm.OnLanguageChanged();

            FinishCopy(job);
            Assert.True(job.Completion.Result.Ok);
            Assert.Equal("", vm.WorldJobText);
            Sta.WaitFor(() => vm.Lines.Count > 0); // соединение свободно — лог читается снова
            Assert.Equal(HostedServerState.Stopped, vm.State);
        }
        finally
        {
            Loc.Instance.SetLanguage(language);
            vm.SetActive(false);
        }
    });

    [Fact]
    public void Overview_OfAnotherProfile_IsNotHeldByThisCopy() => Sta.Run(() =>
    {
        var jobs = new HostedWorldJobs();
        using var other = new HostedFixture();
        var vm = new HostedServerViewModel(null!) { Jobs = jobs };
        vm.OnProfileSwitched(other.Profile);

        var job = StartCopy(jobs);
        Assert.Equal("", vm.WorldJobText); // копия — у другого сервера

        vm.OnProfileSwitched(_host.Profile); // перешли на сервер, у которого она идёт, — строка сразу
        Assert.Equal(HostedWorldBusy.Describe(job), vm.WorldJobText);
        FinishCopy(job);
        Assert.Equal("", vm.WorldJobText);
    });

    // ---------- «Мои моды»

    [Fact]
    public void MyMods_DuringAWorldCopy_AnswerAtOnce_WithoutQuestionsOrRequests_AndCatchUpAfterIt() => Sta.Run(() =>
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        var config = _host.Server.Text("/serverconfig.json");
        var language = Loc.Instance.Language; // главная модель выставляет язык из настроек — другим тестам его не менять
        var main = new MainViewModel { AutoCheckUpdates = false };
        Loc.Instance.SetLanguage(language);
        try
        {
            main.Profiles.Clear();
            var profile = new ProfileViewModel(_host.Profile, _ => { });
            main.Profiles.Add(profile);
            main.ActiveProfile = profile;
            var mods = main.Mods;
            Sta.WaitFor(() => mods.HostedSource.Resolved is not null && !mods.HostedSource.IsLoading);
            var alpha = mods.Rows.Single(r => r.ModId == "alpha");
            Assert.Equal("", mods.WorldJobText);

            var job = StartCopy(main.HostedWorldJobs);
            Assert.Equal(HostedWorldBusy.Describe(job), mods.WorldJobText);
            var calls = Calls();

            // любой вопрос уронил бы тест: команда должна ответить сразу, а не спрашивать и потом висеть
            using (new ScriptedDialogs())
            {
                Sta.Wait(mods.DeleteCommand.ExecuteAsync(alpha));
                Assert.Equal(HostedWorldBusy.Describe(job), mods.StatusText);

                mods.StatusText = "";
                Sta.Wait(mods.ToggleEnabledCommand.ExecuteAsync(alpha));
                Assert.Equal(HostedWorldBusy.Describe(job), mods.StatusText);

                mods.StatusText = "";
                Sta.Wait(mods.CheckCommand.ExecuteAsync(null));
                Assert.Equal(HostedWorldBusy.Describe(job), mods.StatusText);

                mods.StatusText = "";
                mods.Enqueue([new UpdateQueueItem(_host.Target, "alpha", "alpha", "1.0.0", "1.1.0", null, _host.Zip("alpha_1.1.0.zip", "alpha", "1.1.0"))],
                    ModHistorySource.Update);
                Assert.Equal(HostedWorldBusy.Describe(job), mods.StatusText);
                Assert.Empty(mods.Queue);

                mods.StatusText = "";
                Sta.Wait(mods.AddFilesAsync([_host.Zip("beta_1.0.0.zip", "beta", "1.0.0")]));
                Assert.Equal(HostedWorldBusy.Describe(job), mods.StatusText);

                Sta.Wait(mods.PollHostedAsync()); // опрос «поменяли ли моды со стороны» — молчит
                Sta.Wait(mods.ReloadAsync());     // и перечитать — тоже не идёт, а откладывается
                Assert.Equal(HostedWorldBusy.Describe(job), mods.StatusText);
            }
            Assert.Equal(calls, Calls()); // на сервер — ни запроса
            Assert.True(_host.Server.Has("/Mods/alpha_1.0.0.zip"));
            Assert.False(_host.Server.Has("/Mods/beta_1.0.0.zip"));
            Assert.Equal(config, _host.Server.Text("/serverconfig.json"));
            Assert.True(mods.IsWorldJobRunning(_host.Target));

            FinishCopy(job);
            Assert.Equal("", mods.WorldJobText);
            Assert.False(mods.IsWorldJobRunning(_host.Target));
            // отложенное чтение — сразу после копии, вкладка открыта; строка «идёт копия» в статусе уже неправда
            Sta.WaitFor(() => Calls() > calls && !mods.HostedSource.IsLoading);
            var said = Loc.T("hostedbk.busyHere", "\u0001").Split('\u0001')[0];
            Assert.False(mods.StatusText.StartsWith(said, StringComparison.Ordinal), mods.StatusText);
            Assert.Single(mods.Rows, r => r.ModId == "alpha");
        }
        finally
        {
            _release.Set();
            main.HostedWatch.Stop();
        }
    });
}

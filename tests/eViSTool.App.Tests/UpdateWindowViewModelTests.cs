using eViSTool.Core.AppUpdate;
using eViSTool.Core.Localization;
using eViSTool.Core.Versioning;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.App.Tests;

/// <summary>
/// Окно «Вышла новая версия»: что показывается при открытии, строка состояния, кнопки во время установки, запрет
/// закрытия посреди установки и единственный перезапуск после неё. Релизы выдуманные; установка, перезапуск и браузер
/// подменены делегатами — сети, подмены файлов программы и перезапуска тесты не трогают.
/// </summary>
public sealed class UpdateWindowViewModelTests
{
    /// <summary>Текст релиза по образцу настоящих: английская часть, «---», русская.</summary>
    private const string Notes = """
        **eViSTool 9.9.9: mod history with rollback.**

        **New**
        - Mod history: the History tab in My mods.
        - Announcements can go out at set times.

        **Fixes**
        - Commands now go **one at a time**.

        Update from About.

        ---

        История модов с откатом.

        **Новое**
        - История модов: вкладка «История» в «Моих модах».
        - Объявления можно отправлять в заданное время.

        **Исправления**
        - Команды уходят по одной.

        Обнови из «О программе».
        """;

    private const string RecommendedMark = "<!-- evistool: recommended -->\n";

    /// <summary>Полдень по UTC: в любом поясе от −11 до +11 это тот же день.</summary>
    private static readonly DateTimeOffset Oct12 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static AppRelease Release(string version, string notes = "", DateTimeOffset? published = null) => new()
    {
        Version = ModVersion.ParseOrNull(version)!,
        Tag = "v" + version,
        PageUrl = "https://example.invalid/releases/v" + version,
        Notes = notes,
        Published = published,
        AssetName = $"eViSTool-{version}-win-x64.zip",
        AssetUrl = $"https://example.invalid/eViSTool-{version}-win-x64.zip",
    };

    /// <summary>
    /// Строка списка из Presentation. Полным именем: в пространстве eViSTool.App.Tests короткое имя нашло бы раньше
    /// одноимённую запись окна на WPF (eViSTool.App.UpdateNotesItem), а она сверяется не здесь.
    /// </summary>
    private static Presentation.ViewModels.UpdateNotesItem NotesItem(AppRelease release, bool russian, bool first) =>
        Presentation.ViewModels.UpdateNotesItem.From(release, russian, first);

    private static AboutViewModel About(params AppRelease[] releases)
    {
        var about = new AboutViewModel();
        about.UseReleases(releases);
        return about;
    }

    /// <summary>Что окно попросило у внешнего мира — по порядку.</summary>
    private sealed class Probe
    {
        public List<string> Log { get; } = [];
        public List<string> Restarted { get; } = [];
        public List<string> Opened { get; } = [];
        public int Installs { get; set; }
    }

    /// <summary>Модель с подменами; без установки (install null) нажатие «Обновить сейчас» роняет тест.</summary>
    private static UpdateWindowViewModel Model(AboutViewModel about, Probe probe, Func<Task<AppRelease?>>? install = null)
    {
        var vm = new UpdateWindowViewModel(about, () => probe.Log.Add("skip"),
            async () =>
            {
                probe.Installs++;
                return install is null ? throw new Xunit.Sdk.XunitException("установка в этом тесте не ожидалась") : await install();
            },
            exe =>
            {
                probe.Log.Add("restart");
                probe.Restarted.Add(exe);
            },
            probe.Opened.Add);
        vm.CloseRequested += () => probe.Log.Add("close");
        return vm;
    }

    private static void AssertButtons(UpdateWindowViewModel vm, bool enabled)
    {
        Assert.Equal(enabled, vm.CanAct);
        Assert.Equal(enabled, vm.InstallCommand.CanExecute(null));
        Assert.Equal(enabled, vm.LaterCommand.CanExecute(null));
        Assert.Equal(enabled, vm.SkipCommand.CanExecute(null));
    }

    // ---- открытие

    [Fact]
    public void Opens_WithBothVersions_AndNotesOfEveryMissedRelease_NewestFirst()
    {
        var about = About(Release("9.9.9", Notes, Oct12), Release("9.9.8", Notes), Release("9.9.7"));
        var vm = Model(about, new Probe());

        Assert.Equal(about.AppVersion, vm.OldVersion);
        Assert.Equal("9.9.9", vm.NewVersion);
        Assert.False(vm.IsRecommended);
        Assert.True(vm.CanSkip);
        Assert.Equal(["9.9.9", "9.9.8", "9.9.7"], vm.Releases.Select(r => r.Version));
        // черта-разделитель — над всеми, кроме верхней
        Assert.Equal([true, false, false], vm.Releases.Select(r => r.IsFirst));
        Assert.Equal("", vm.Status);
        Assert.False(vm.HasStatus);
        Assert.True(vm.CanClose);
        AssertButtons(vm, enabled: true);
    }

    [Fact]
    public void Notes_FollowTheInterfaceLanguage()
    {
        var before = Loc.Instance.Language;
        try
        {
            Loc.Instance.SetLanguage("ru");
            var ru = Model(About(Release("9.9.9", Notes, Oct12)), new Probe()).Releases.Single();
            Assert.Equal("История модов с откатом", ru.Summary);
            Assert.Equal("12 октября", ru.Date);

            Loc.Instance.SetLanguage("en");
            var en = Model(About(Release("9.9.9", Notes, Oct12)), new Probe()).Releases.Single();
            Assert.Equal("Mod history with rollback", en.Summary);
            Assert.Equal("October 12", en.Date);
        }
        finally
        {
            Loc.Instance.SetLanguage(before);
        }
    }

    [Fact]
    public void RecommendedAmongMissed_HidesSkip_AndMarksThatVersion()
    {
        // рекомендуемая — не самая новая: пропустить нельзя всё равно, номер новой версии — тёплым цветом
        var vm = Model(About(Release("9.9.9", Notes), Release("9.9.8", RecommendedMark + Notes), Release("9.9.7")), new Probe());

        Assert.True(vm.IsRecommended);
        Assert.False(vm.CanSkip);
        Assert.Equal([false, true, false], vm.Releases.Select(r => r.Recommended));
        Assert.Equal("9.9.9", vm.NewVersion);
    }

    [Fact]
    public void NothingFound_ShowsNoNewVersion()
    {
        var vm = Model(About(), new Probe());
        Assert.Equal("", vm.NewVersion);
        Assert.Empty(vm.Releases);
        Assert.False(vm.IsRecommended);
    }

    [Fact]
    public void DataIsTakenOnce_WhenTheWindowOpens()
    {
        // фоновая проверка может потом сбросить найденное — открытое окно показывает то, с чем открылось (как на WPF)
        var about = About(Release("9.9.9", RecommendedMark + Notes));
        var vm = Model(about, new Probe());
        about.UseReleases([]);

        Assert.Equal("9.9.9", vm.NewVersion);
        Assert.True(vm.IsRecommended);
        Assert.Single(vm.Releases);
    }

    // ---- строка состояния

    [Fact]
    public void Status_StartsEmpty_ThenFollowsAbout()
    {
        var about = About(Release("9.9.9"));
        about.UpdateStatus = "Доступна версия 9.9.9."; // надпись от прошлой проверки в «О программе» — окну не нужна
        var vm = Model(about, new Probe());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal("", vm.Status);
        Assert.False(vm.HasStatus);

        about.UpdateStatus = "Скачиваю… 40%";
        Assert.Equal("Скачиваю… 40%", vm.Status);
        Assert.True(vm.HasStatus);
        Assert.Contains(nameof(UpdateWindowViewModel.Status), changed);
        Assert.Contains(nameof(UpdateWindowViewModel.HasStatus), changed);

        about.UpdateStatus = "";
        Assert.False(vm.HasStatus);
    }

    [Fact]
    public void Dispose_StopsFollowingAbout()
    {
        var about = About(Release("9.9.9"));
        var vm = Model(about, new Probe());
        vm.Dispose();

        about.UpdateStatus = "Скачиваю… 40%";
        about.IsUpdating = true;
        Assert.Equal("", vm.Status);
    }

    // ---- установка идёт где угодно: кнопки выключены, закрыть нельзя

    [Fact]
    public void UpdatingFromAbout_DisablesButtons_AndBlocksClosing()
    {
        var about = About(Release("9.9.9"));
        var vm = Model(about, new Probe());
        var canExecuteChanged = 0;
        vm.LaterCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        about.IsUpdating = true;
        AssertButtons(vm, enabled: false);
        Assert.False(vm.CanClose);
        Assert.True(canExecuteChanged > 0);
        Assert.Contains(nameof(UpdateWindowViewModel.CanAct), changed);
        Assert.Contains(nameof(UpdateWindowViewModel.CanClose), changed);

        about.IsUpdating = false;
        AssertButtons(vm, enabled: true);
        Assert.True(vm.CanClose);
    }

    // ---- «Обновить сейчас»

    [Fact]
    public async Task Install_Succeeded_ClosesWindow_ThenRestartsOnce()
    {
        var about = About(Release("9.9.9"));
        var probe = new Probe();
        var done = new TaskCompletionSource<AppRelease?>();
        var vm = Model(about, probe, async () =>
        {
            about.IsUpdating = true; // как AboutViewModel.InstallAsync: на время установки
            try { return await done.Task; }
            finally { about.IsUpdating = false; }
        });

        var run = vm.InstallCommand.ExecuteAsync(null);
        // идёт установка: кнопки выключены, окно не закрыть, ни закрытия, ни перезапуска ещё нет
        AssertButtons(vm, enabled: false);
        Assert.False(vm.CanClose);
        Assert.Empty(probe.Log);

        done.SetResult(about.AvailableUpdate);
        await run;

        // сначала окно закрывается, потом программа перезапускается — ровно один раз и главным exe из папки программы
        Assert.Equal(["close", "restart"], probe.Log);
        Assert.Equal([Path.Combine(AppContext.BaseDirectory, AppUpdater.MainExe)], probe.Restarted);
        Assert.Equal(UpdateWindowViewModel.MainExePath, probe.Restarted.Single());
        Assert.Equal(1, probe.Installs);
    }

    [Fact]
    public async Task Install_Failed_StaysOpen_AndCanBeRetried()
    {
        var about = About(Release("9.9.9"));
        var probe = new Probe();
        var vm = Model(about, probe, () =>
        {
            about.UpdateStatus = "Не удалось обновить: нет сети"; // причина — в строке состояния
            return Task.FromResult<AppRelease?>(null);
        });

        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Empty(probe.Log); // ни закрытия, ни перезапуска
        Assert.Equal("Не удалось обновить: нет сети", vm.Status);
        AssertButtons(vm, enabled: true);
        Assert.True(vm.CanClose);

        await vm.InstallCommand.ExecuteAsync(null); // ещё раз
        Assert.Equal(2, probe.Installs);
        Assert.Empty(probe.Log);
    }

    [Fact]
    public async Task Install_Threw_ButtonsComeBack()
    {
        var probe = new Probe();
        var vm = Model(About(Release("9.9.9")), probe, () => Task.FromException<AppRelease?>(new IOException("диск занят")));

        await Assert.ThrowsAsync<IOException>(() => vm.InstallCommand.ExecuteAsync(null));
        Assert.Empty(probe.Log);
        AssertButtons(vm, enabled: true);
    }

    [Fact]
    public async Task Install_ByDefault_IsAboutInstall_WhichDoesNotRestartByItself()
    {
        // установка по умолчанию — AboutViewModel.InstallAsync; ставить нечего — она отвечает null сразу, без сети,
        // и окно остаётся открытым: перезапуск только у окна и только после настоящей установки
        var probe = new Probe();
        var vm = new UpdateWindowViewModel(About(), () => probe.Log.Add("skip"), restart: exe => probe.Log.Add("restart"));
        vm.CloseRequested += () => probe.Log.Add("close");

        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Empty(probe.Log);
        AssertButtons(vm, enabled: true);
    }

    // ---- «Позже», «Пропустить», ссылка на страницу релиза

    [Fact]
    public void Later_Closes_WithoutSkipping()
    {
        var probe = new Probe();
        var vm = Model(About(Release("9.9.9")), probe);

        vm.LaterCommand.Execute(null);
        Assert.Equal(["close"], probe.Log);
    }

    [Fact]
    public void Skip_RemembersTheVersion_ThenCloses()
    {
        var probe = new Probe();
        var vm = Model(About(Release("9.9.9")), probe);

        vm.SkipCommand.Execute(null);
        Assert.Equal(["skip", "close"], probe.Log);
    }

    [Fact]
    public void OpenPage_OpensOnlyAnActualAddress()
    {
        var probe = new Probe();
        var vm = Model(About(Release("9.9.9")), probe);

        vm.OpenPageCommand.Execute("https://example.invalid/releases/v9.9.9");
        vm.OpenPageCommand.Execute("");
        vm.OpenPageCommand.Execute(null);
        Assert.Equal(["https://example.invalid/releases/v9.9.9"], probe.Opened);
        Assert.Empty(probe.Log);
    }

    // ---- одна версия в списке

    [Fact]
    public void NotesItem_Russian_TakesTheRussianPart_AndRussianDate()
    {
        var item = NotesItem(Release("9.9.9", Notes, Oct12), russian: true, first: true);

        Assert.Equal("9.9.9", item.Version);
        Assert.Equal("12 октября", item.Date);
        Assert.Equal("История модов с откатом", item.Summary);
        Assert.Equal(["Новое", "Исправления"], item.Sections.Select(s => s.Heading));
        Assert.Equal("Объявления можно отправлять в заданное время.", item.Sections[0].Items[1]);
        Assert.Equal("https://example.invalid/releases/v9.9.9", item.PageUrl);
        Assert.True(item.IsFirst);
        Assert.False(item.Recommended);
        Assert.False(item.IsEmpty);
    }

    [Fact]
    public void NotesItem_English_TakesTheEnglishPart_AndEnglishDate()
    {
        var item = NotesItem(Release("9.9.9", Notes, Oct12), russian: false, first: false);

        Assert.Equal("October 12", item.Date);
        Assert.Equal("Mod history with rollback", item.Summary);
        Assert.Equal(["New", "Fixes"], item.Sections.Select(s => s.Heading));
        Assert.Equal("Commands now go one at a time.", item.Sections[1].Items.Single());
        Assert.False(item.IsFirst);
    }

    [Fact]
    public void NotesItem_WithoutTextAndDate_IsEmpty_SoTheWindowLinksToThePage()
    {
        var item = NotesItem(Release("9.9.7"), russian: true, first: false);

        Assert.Equal("", item.Date);
        Assert.Null(item.Summary);
        Assert.Empty(item.Sections);
        Assert.True(item.IsEmpty);
    }

    [Fact]
    public void NotesItem_OnlyTheRecommendedMark_IsEmptyButRecommended()
    {
        // пометка — служебная строка, читать в ней нечего: тег «рекомендуется» и ссылка на страницу
        var item = NotesItem(Release("9.9.8", RecommendedMark), russian: true, first: true);

        Assert.True(item.Recommended);
        Assert.True(item.IsEmpty);
    }

    // ---- AboutViewModel.UseReleases (для снимков и этих тестов)

    [Fact]
    public void UseReleases_SetsWhatTheCheckWouldFind_WithoutRaisingUpdateFound()
    {
        var about = new AboutViewModel();
        var found = 0;
        about.UpdateFound += () => found++;
        var changed = new List<string?>();
        about.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        AppRelease[] releases = [Release("9.9.9"), Release("9.9.8", RecommendedMark)];

        about.UseReleases(releases);

        Assert.Same(releases, about.MissedReleases);
        Assert.Same(releases[0], about.AvailableUpdate);
        Assert.True(about.HasUpdate);
        Assert.True(about.IsRecommended);
        Assert.Contains(nameof(AboutViewModel.IsRecommended), changed);
        Assert.Contains(nameof(AboutViewModel.AvailableUpdate), changed);
        Assert.Equal(0, found);

        about.UseReleases([]);
        Assert.Null(about.AvailableUpdate);
        Assert.False(about.IsRecommended);
    }
}

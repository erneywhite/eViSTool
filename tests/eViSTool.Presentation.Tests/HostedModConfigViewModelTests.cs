using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Tests.Hosting;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Вкладка «Настройки модов» для сервера у хостера: файлы ModConfig по FTP (поддельный сервер «как G-Portal» над
/// временной папкой), «Показать файл» скрыт, перед записью, откатом и сбросом — вопрос «останови сервер в панели
/// хостера» с общей с «Моими модами» памятью ответа; «Нет» ничего не пишет на сервер; что операция заодно починила,
/// окно говорит. Главная модель настоящая.
/// </summary>
public sealed class HostedModConfigViewModelTests : IDisposable
{
    private const string CarryText = "{\n  \"Range\": 3\n}\n";
    private const string Carry = "/ModConfig/carryon.json";
    private const string Tmp = Carry + ".evistool.tmp";
    private const string Bak = Carry + ".evistool.bak";

    private readonly HostedFixture _host = new();
    private readonly IAppWindows? _oldWindows = AppWindows.IsSet ? AppWindows.Current : null;

    public HostedModConfigViewModelTests()
    {
        _host.GPortalLayout();
        _host.PutMod("/Mods/carryon_1.0.0.zip", "carryon", "1.0.0");
        _host.Server.Put("/ModConfig/carryon.json", CarryText);
        _host.Server.Put("/ModConfig/AutoMapMarkers/settings.json", "{}");
        _host.Server.Put("/ModConfig/unknown.cfg", "x = 1");
    }

    public void Dispose()
    {
        AppWindows.Current = _oldWindows!;
        _host.Dispose();
    }

    /// <summary>Главная модель с профилем у хостера; открыта вкладка «Настройки» — моды сами не читаются.</summary>
    private MainViewModel Main()
    {
        var language = Loc.Instance.Language; // главная модель выставляет язык из настроек — другим тестам его не менять
        var main = new MainViewModel { AutoCheckUpdates = false, SelectedTab = AppTab.Settings };
        Loc.Instance.SetLanguage(language);
        main.Profiles.Clear();
        var profile = new ProfileViewModel(_host.Profile, _ => { });
        main.Profiles.Add(profile);
        main.ActiveProfile = profile;
        return main;
    }

    /// <summary>Вкладка открыта и файл выбран (текст прочитан с сервера).</summary>
    private static ModConfigViewModel Opened(MainViewModel main, string file = "carryon.json")
    {
        var model = main.ModConfig;
        Sta.Wait(model.LoadAsync());
        model.Selected = model.Rows.Single(r => r.File.RelativePath == file);
        Sta.WaitFor(() => model.Text.Length > 0);
        return model;
    }

    private string ServerText => _host.Server.Text("/ModConfig/carryon.json");

    [Fact]
    public void Open_ListsTheServersFiles_WithoutShowFile() => Sta.Run(() =>
    {
        var main = Main();

        var model = Opened(main);

        Assert.Equal("", model.Notice);
        Assert.Equal(["carryon", "AutoMapMarkers/settings.json", "unknown.cfg"], model.Rows.Select(r => r.Title));
        Assert.Equal(CarryText, model.Text);
        Assert.False(model.IsDirty);
        Assert.False(model.CanShowFile);                      // файл на FTP — на этой машине показывать нечего
        Assert.Equal("/ModConfig/carryon.json", model.PathText); // путь на сервере
        Assert.Empty(_host.Server.Writes);
    });

    [Fact]
    public void Save_AsksToStopTheServer_AndNoWritesNothing() => Sta.Run(() =>
    {
        var model = Opened(Main());
        model.Text = "{ \"Range\": 5 }";

        using (var dialogs = new ScriptedDialogs(Answer.No))
        {
            Sta.Wait(model.SaveCommand.ExecuteAsync(null));
            Assert.Equal([Loc.T("mcfg.hostedStopAsk", _host.Profile.Name)], dialogs.Asked);
        }

        Assert.Equal(CarryText, ServerText);
        Assert.Empty(_host.Server.Writes);
        Assert.True(model.IsDirty); // правка осталась в поле
    });

    [Fact]
    public void Save_Yes_WritesToTheServer_AndTheAnswerIsSharedWithMyMods() => Sta.Run(() =>
    {
        var main = Main();
        var model = Opened(main);
        model.Text = "{ \"Range\": 5 }";

        using (var dialogs = new ScriptedDialogs(Answer.Yes))
        {
            Sta.Wait(model.SaveCommand.ExecuteAsync(null));
            Assert.Single(dialogs.Asked);
        }

        Assert.Equal("{ \"Range\": 5 }", ServerText);
        Assert.False(model.IsDirty);
        Assert.Equal(1, model.Versions);
        Assert.Equal(Loc.T("mcfg.saved", "carryon.json"), model.Status);

        // «Да» помнится 10 минут для профиля — и в «Моих модах», и при следующей записи настроек
        using (var quiet = new ScriptedDialogs())
        {
            Assert.True(main.Mods.ConfirmIfRunning(_host.Target));
            model.Text = "{ \"Range\": 6 }";
            Sta.Wait(model.SaveCommand.ExecuteAsync(null));
            Assert.Empty(quiet.Asked);
        }
        Assert.Equal("{ \"Range\": 6 }", ServerText);
    });

    [Fact]
    public void Save_ChangedOnTheServerSinceOpened_AsksBeforeOverwriting() => Sta.Run(() =>
    {
        var model = Opened(Main());
        var local = _host.Server.Put("/ModConfig/carryon.json", "{ \"Range\": 9 }");
        File.SetLastWriteTimeUtc(local, DateTime.UtcNow.AddMinutes(3));
        model.Text = "{ \"Range\": 5 }";

        using (var dialogs = new ScriptedDialogs(Answer.Yes, Answer.No))
        {
            Sta.Wait(model.SaveCommand.ExecuteAsync(null));
            Assert.Equal([Loc.T("mcfg.hostedStopAsk", _host.Profile.Name), Loc.T("mcfg.changedOutside", "carryon.json")], dialogs.Asked);
        }

        Assert.Equal("{ \"Range\": 9 }", ServerText);
        Assert.Empty(_host.Server.Writes);
    });

    [Fact]
    public void ResetAndRestore_AskToStopTheServer_NoKeepsTheFile() => Sta.Run(() =>
    {
        var model = Opened(Main());

        using (var dialogs = new ScriptedDialogs(Answer.Yes, Answer.No))
        {
            Sta.Wait(model.ResetCommand.ExecuteAsync(null));
            Assert.Equal([Loc.T("mcfg.askReset", "carryon.json"), Loc.T("mcfg.hostedStopAsk", _host.Profile.Name)], dialogs.Asked);
        }
        Assert.Equal(CarryText, ServerText);
        Assert.Empty(_host.Server.Writes);

        using (var dialogs = new ScriptedDialogs(Answer.Yes, Answer.Yes))
            Sta.Wait(model.ResetCommand.ExecuteAsync(null));
        Assert.False(_host.Server.Has("/ModConfig/carryon.json"));
        Assert.Equal(1, model.Versions);
        Assert.Equal(Loc.T("mcfg.resetDone", "carryon.json"), model.Status);

        // «Да» на остановку ещё помнится — шаг назад без вопросов
        using (var quiet = new ScriptedDialogs())
            Sta.Wait(model.RestorePreviousCommand.ExecuteAsync(null));
        Assert.Equal(CarryText, ServerText);
        Assert.Equal(CarryText, model.Text);
    });

    [Fact]
    public void Restore_AsksToStopTheServer_NoWritesNothing_YesUploadsThePreviousVersion() => Sta.Run(() =>
    {
        // прежняя версия уже есть на этой машине (сохраняли раньше)
        ModConfigBackups.ForProfile(_host.Profile).Keep(System.Text.Encoding.UTF8.GetBytes("{ \"Range\": 1 }"), "carryon.json");
        var model = Opened(Main());
        Assert.Equal(1, model.Versions);

        using (var dialogs = new ScriptedDialogs(Answer.No))
        {
            Sta.Wait(model.RestorePreviousCommand.ExecuteAsync(null));
            Assert.Equal([Loc.T("mcfg.hostedStopAsk", _host.Profile.Name)], dialogs.Asked);
        }
        Assert.Equal(CarryText, ServerText);
        Assert.Empty(_host.Server.Writes);
        Assert.Equal(1, model.Versions);

        using (var dialogs = new ScriptedDialogs(Answer.Yes))
            Sta.Wait(model.RestorePreviousCommand.ExecuteAsync(null));
        Assert.Equal("{ \"Range\": 1 }", ServerText);
        Assert.Equal("{ \"Range\": 1 }", model.Text);
        Assert.Equal(0, model.Versions);
    });

    [Fact]
    public void SwitchingToAnotherFile_WithEdits_SavesThemToTheServer_AfterTheStopQuestion() => Sta.Run(() =>
    {
        var model = Opened(Main());
        model.Text = "{ \"Range\": 5 }";

        using (var dialogs = new ScriptedDialogs(Answer.Yes, Answer.Yes))
        {
            model.Selected = model.Rows.Single(r => r.File.RelativePath == "unknown.cfg");
            Sta.WaitFor(() => model.Text == "x = 1" && model.Status == Loc.T("mcfg.saved", "carryon.json"));
            Assert.Equal([Loc.T("mcfg.askSave", "carryon.json"), Loc.T("mcfg.hostedStopAsk", _host.Profile.Name)], dialogs.Asked);
            Assert.Empty(dialogs.Warned);
        }
        Assert.Equal("{ \"Range\": 5 }", ServerText);
        Assert.False(model.IsDirty); // открыт уже другой файл, без правок
    });

    [Fact]
    public void AFileReturnedAfterABrokenSave_IsSaidInTheWindow_AlsoWithTheFileOpen() => Sta.Run(() =>
    {
        var model = Opened(Main());
        // запись сорвалась посередине, и вернуть прежний файл тоже не вышло: на сервере остался только bak
        _host.Server.FailOn = (op, path) => op == "rename" && (path == Tmp && _host.Server.Has(Bak) || path == Bak);
        model.Text = "{ \"Range\": 5 }";
        using (var dialogs = new ScriptedDialogs(Answer.Yes))
        {
            Sta.Wait(model.SaveCommand.ExecuteAsync(null));
            Assert.Single(dialogs.Warned); // «не удалось сохранить»
        }
        _host.Server.FailOn = null;
        Assert.False(_host.Server.Has(Carry));

        // «Обновить», файл открыт: список возвращает файл на место, открытый файл перечитывается — окно говорит об этом раз
        using (var dialogs = new ScriptedDialogs())
        {
            Sta.Wait(model.RefreshCommand.ExecuteAsync(null));
            Sta.WaitFor(() => model.Text == CarryText);
            Assert.Equal([Loc.T("mcfg.hostedRestored", "carryon.json")], dialogs.Warned);
        }
        Assert.Equal(CarryText, ServerText);
        Assert.False(_host.Server.Has(Bak));
    });

    [Fact]
    public void LeavingWithEdits_Yes_ButTheServerIsRunning_StaysAndWritesNothing() => Sta.Run(() =>
    {
        var model = Opened(Main());
        model.Text = "{ \"Range\": 5 }";

        using (var dialogs = new ScriptedDialogs(Answer.Yes, Answer.No))
            Assert.False(model.ConfirmLeave());

        Assert.Equal(CarryText, ServerText);
        Assert.Empty(_host.Server.Writes);
        Assert.True(model.IsDirty);
    });

    [Fact]
    public void LeavingWithEdits_WhileTheConnectionIsBusy_SaysSoInWords_AndStays() => Sta.Run(() =>
    {
        var model = Opened(Main());
        model.Text = "{ \"Range\": 5 }";
        // очередь соединения профиля занята (например, долгим сканом модов)
        var busy = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = HostedSessions.UseAsync(_host.Profile.Id, _host.Endpoint, _ => busy.Task, CancellationToken.None);
        var wait = ModConfigViewModel.LeaveSaveWait;
        ModConfigViewModel.LeaveSaveWait = TimeSpan.FromMilliseconds(300);
        try
        {
            using var dialogs = new ScriptedDialogs(Answer.Yes, Answer.Yes);
            Assert.False(model.ConfirmLeave());
            // своими словами, а не «The operation was canceled»
            Assert.Equal([Loc.T("mcfg.hostedSaveTimeout", "carryon.json")], dialogs.Warned);
        }
        finally
        {
            ModConfigViewModel.LeaveSaveWait = wait;
            busy.SetResult(0);
            Sta.Wait(holder);
        }
        Assert.True(model.IsDirty);
        Assert.Equal(CarryText, ServerText);
        Assert.Empty(_host.Server.Writes);
    });

    [Fact]
    public void TheModCard_GetsItsSettingsLink_FromTheListAlreadyRead() => Sta.Run(() =>
    {
        var main = Main();
        main.SelectedTab = AppTab.Mods;
        Sta.WaitFor(() => main.Mods.HostedSource.Resolved is not null && !main.Mods.HostedSource.IsLoading);
        Assert.Equal(0, main.Mods.ConfigCount("carryon")); // «Настройки модов» в этом окне ещё не открывали

        main.SelectedTab = AppTab.ModConfig;
        Sta.WaitFor(() => main.ModConfig.Rows.Count == 3 && !main.ModConfig.IsLoading);

        Assert.Equal(1, main.Mods.ConfigCount("carryon"));
        // и после перечитывания модов ссылка остаётся — по уже прочитанному списку, ModConfig заново не листается
        var modConfigLists = 0;
        _host.Server.OnBefore = (op, path) =>
        {
            if (op == "list" && path.StartsWith("/ModConfig", StringComparison.Ordinal)) modConfigLists++;
        };
        Sta.Wait(main.Mods.HostedSource.ReloadAsync());
        Assert.Equal(1, main.Mods.ConfigCount("carryon"));
        Assert.Equal(0, modConfigLists);
    });

    [Fact]
    public void ServerNotReachable_SaysWhyInWords() => Sta.Run(() =>
    {
        _host.Server.FailOn = (op, _) => op == "connect";
        _host.Server.FailKind = RemoteFailure.Auth;
        var model = Main().ModConfig;

        Sta.Wait(model.LoadAsync());

        Assert.Equal(Loc.T("mcfg.hostedFailed", Loc.T("hosted.errAuth")), model.Notice);
        Assert.Empty(model.Rows);
    });

    [Fact]
    public void PasswordNotReadableHere_SaysWhatToDo_WithoutGoingToTheServer() => Sta.Run(() =>
    {
        _host.Settings.Password = "not-a-protected-value";
        var model = Main().ModConfig;

        Sta.Wait(model.LoadAsync());

        Assert.Equal(Loc.T("hosted.errDecrypt"), model.Notice);
        Assert.Equal(0, _host.Server.Connects);
    });
}

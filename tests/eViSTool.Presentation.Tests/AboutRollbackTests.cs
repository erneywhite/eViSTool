using System.IO.Compression;
using eViSTool.Core.AppUpdate;
using eViSTool.Core.Localization;
using eViSTool.Core.Versioning;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// «О программе» → «Вернуться на прежнюю версию»: строка и кнопка, пока прежняя версия хранится, вопрос перед откатом,
/// перезапуск и пропуск версии, с которой ушли. Папка программы — временная, обновление в неё ставится настоящим
/// AppUpdater.Install (без сети), перезапуск только считается.
/// </summary>
public sealed class AboutRollbackTests : IDisposable
{
    private const string Previous = "0.0.1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-about-rollback-" + Guid.NewGuid().ToString("N"));
    private readonly string _app;

    public AboutRollbackTests()
    {
        _app = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        File.WriteAllText(Path.Combine(_app, AppUpdater.MainExe), "main old");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string MainText => File.ReadAllText(Path.Combine(_app, AppUpdater.MainExe));

    /// <summary>Модель над временной папкой, в которую обновились с <see cref="Previous"/> на версию этой сборки.</summary>
    private AboutViewModel UpdatedAbout()
    {
        var about = new AboutViewModel { AppDir = _app };
        var zip = Path.Combine(_root, "update.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry(AppUpdater.MainExe).Open()))
            writer.Write("main new");
        AppUpdater.Install(zip, _app, Previous, about.AppVersion);
        about.RefreshRollback();
        return about;
    }

    [Fact]
    public void WhileThePreviousVersionIsKept_ShowsTheLineAndTheButton() => Sta.Run(() =>
    {
        var about = UpdatedAbout();

        Assert.True(about.HasPreviousVersion);
        Assert.Equal(Loc.T("about.rollbackAvailable", Previous), about.PreviousVersionText);
        Assert.True(about.RollBackCommand.CanExecute(null));
    });

    [Fact]
    public void WithoutAPreviousVersion_NothingIsShown() => Sta.Run(() =>
    {
        var about = new AboutViewModel { AppDir = _app };
        about.RefreshRollback();

        Assert.False(about.HasPreviousVersion);
        Assert.Equal("", about.PreviousVersionText);
        Assert.False(about.RollBackCommand.CanExecute(null));
    });

    // «нет» на вопрос — файлы и версия на месте, перезапуска нет
    [Fact]
    public void RollBack_AsksFirst() => Sta.Run(() =>
    {
        var about = UpdatedAbout();
        var rolledBack = new List<string>();
        about.RolledBack = rolledBack.Add;
        using var dialogs = new ScriptedDialogs(Answer.No);

        Sta.Wait(about.RollBackCommand.ExecuteAsync(null));

        Assert.Equal(Loc.T("about.rollbackAsk", Previous, about.AppVersion), Assert.Single(dialogs.Asked));
        Assert.Equal("main new", MainText);
        Assert.True(about.HasPreviousVersion);
        Assert.Equal(0, dialogs.Restarts);
        Assert.Empty(rolledBack);
    });

    // «да» — прежняя версия на месте, окно перезапускается, версию, с которой ушли, больше не предлагать
    [Fact]
    public void RollBack_SwapsTheFiles_Restarts_AndSkipsTheVersionLeft() => Sta.Run(() =>
    {
        var about = UpdatedAbout();
        var rolledBack = new List<string>();
        about.RolledBack = rolledBack.Add;
        using var dialogs = new ScriptedDialogs(Answer.Yes);

        Sta.Wait(about.RollBackCommand.ExecuteAsync(null));

        Assert.Equal("main old", MainText);
        Assert.Equal(1, dialogs.Restarts);
        Assert.Equal([about.AppVersion], rolledBack);
        Assert.False(about.HasPreviousVersion);
        Assert.False(about.IsUpdating);
        Assert.Equal(Loc.T("about.rollbackDone", Previous), about.UpdateStatus);
    });

    // прежнюю версию убрали, пока окно было открыто, — отказ словами, перезапуска нет
    [Fact]
    public void RollBack_WhenThePreviousVersionIsGone_SaysSo() => Sta.Run(() =>
    {
        var about = UpdatedAbout();
        AppUpdater.CleanupOld(_app);
        using var dialogs = new ScriptedDialogs(Answer.Yes);

        Sta.Wait(about.RollBackCommand.ExecuteAsync(null));

        Assert.Equal("main new", MainText);
        Assert.Equal(0, dialogs.Restarts);
        Assert.Equal(Loc.T("about.rollbackFailed", Loc.T("update.noPrevious")), about.UpdateStatus);
        Assert.False(about.HasPreviousVersion);
    });

    /// <summary>Окна программы: показ «Вышла новая версия» только считается.</summary>
    private static List<object?> CountPrompts()
    {
        var shown = new List<object?>();
        AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of(
            (nameof(IAppWindows.ShowUpdatePrompt), args =>
            {
                shown.Add(args[0]);
                return null;
            }));
        return shown;
    }

    private static AppRelease Release(string version, bool recommended) => new()
    {
        Version = ModVersion.ParseOrNull(version)!,
        Tag = "v" + version,
        Notes = recommended ? "<!-- evistool: recommended -->" : "",
        AssetName = $"eViSTool-{version}-win-x64.zip",
        AssetUrl = $"https://example.invalid/eViSTool-{version}-win-x64.zip",
    };

    // с версии вернулись — окно «Вышла новая версия» про неё молчит, даже про рекомендуемую; вышла новее — показывает
    [Fact]
    public void VersionRolledBackFrom_IsNotOffered_EvenRecommended() => Sta.Run(() =>
    {
        var old = AppWindows.IsSet ? AppWindows.Current : null;
        try
        {
            var shown = CountPrompts();
            var about = new AboutViewModel();
            about.UseReleases([Release("9.1.0", recommended: true)]);
            new UpdatePrompt(about, () => "9.1.0", _ => { }, () => false, () => "9.1.0").Show();
            Assert.Empty(shown);

            about.UseReleases([Release("9.2.0", recommended: false), Release("9.1.0", recommended: true)]);
            new UpdatePrompt(about, () => "9.1.0", _ => { }, () => false, () => "9.1.0").Show();
            Assert.Single(shown);
        }
        finally
        {
            AppWindows.Current = old!;
        }
    });
}

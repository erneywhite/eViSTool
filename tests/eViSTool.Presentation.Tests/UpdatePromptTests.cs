using eViSTool.Core.AppUpdate;
using eViSTool.Core.Versioning;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Окно «Вышла новая версия»: раз за запуск, не поверх игры, пропущенную версию не предлагаем — кроме рекомендуемой.
/// Обновления «находятся» готовым списком (без GitHub), окна программы подменены: показ окна только считается.
/// </summary>
public sealed class UpdatePromptTests : IDisposable
{
    private readonly IAppWindows? _oldWindows = AppWindows.IsSet ? AppWindows.Current : null;

    public void Dispose() => AppWindows.Current = _oldWindows!; // null — не было: следующий тест поставит своё

    private static AppRelease Release(string version, bool recommended = false) => new()
    {
        Version = ModVersion.ParseOrNull(version)!,
        Tag = "v" + version,
        Notes = recommended ? "<!-- evistool: recommended -->" : "",
        AssetName = $"eViSTool-{version}-win-x64.zip",
        AssetUrl = $"https://example.invalid/eViSTool-{version}-win-x64.zip",
    };

    private static AboutViewModel About(params AppRelease[] releases)
    {
        var about = new AboutViewModel();
        about.UseReleases(releases);
        return about;
    }

    /// <summary>Окна программы: показ «Вышла новая версия» запоминается (с кнопкой «Пропустить»), любое другое — ошибка теста.</summary>
    private sealed class Shown
    {
        public List<Action> Prompts { get; } = [];

        public Shown() => AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of(
            (nameof(IAppWindows.ShowUpdatePrompt), args =>
            {
                Prompts.Add((Action)args[1]!);
                return null;
            }));
    }

    [Fact]
    public void NoUpdate_ShowsNothing() => Sta.Run(() =>
    {
        var shown = new Shown();
        var prompt = new UpdatePrompt(About(), () => null, _ => { }, () => false);

        prompt.Show();

        Assert.Empty(shown.Prompts);
    });

    [Fact]
    public void Update_IsShownOncePerRun() => Sta.Run(() =>
    {
        var shown = new Shown();
        var prompt = new UpdatePrompt(About(Release("9.1.0")), () => null, _ => { }, () => false);

        prompt.Show();
        prompt.Show(); // закрыли игру, нашлось ещё раз — второй раз за запуск не показываем

        Assert.Single(shown.Prompts);
    });

    // не поверх игры: пока она идёт, ждём; закрыли — показываем
    [Fact]
    public void WhileTheGameIsBusy_Waits_ThenShows() => Sta.Run(() =>
    {
        var shown = new Shown();
        var busy = true;
        var prompt = new UpdatePrompt(About(Release("9.1.0")), () => null, _ => { }, () => busy);

        prompt.Show();
        Assert.Empty(shown.Prompts);

        busy = false;
        prompt.Show();
        Assert.Single(shown.Prompts);
    });

    [Fact]
    public void SkippedVersion_IsNotOffered() => Sta.Run(() =>
    {
        var shown = new Shown();
        var prompt = new UpdatePrompt(About(Release("9.1.0")), () => "9.1.0", _ => { }, () => false);

        prompt.Show();

        Assert.Empty(shown.Prompts);
    });

    // рекомендуемую предлагаем и пропущенной
    [Fact]
    public void SkippedVersion_IsOffered_WhenRecommended() => Sta.Run(() =>
    {
        var shown = new Shown();
        var prompt = new UpdatePrompt(About(Release("9.1.0", recommended: true)), () => "9.1.0", _ => { }, () => false);

        prompt.Show();

        Assert.Single(shown.Prompts);
    });

    // «Пропустить» в окне запоминает именно предложенную версию
    [Fact]
    public void Skip_RemembersTheOfferedVersion() => Sta.Run(() =>
    {
        var shown = new Shown();
        var skipped = new List<string>();
        var prompt = new UpdatePrompt(About(Release("9.2.0"), Release("9.1.0")), () => null, skipped.Add, () => false);

        prompt.Show();
        Assert.Single(shown.Prompts)();

        Assert.Equal(new[] { "9.2.0" }, skipped);
    });
}

using eViSTool.Core;
using eViSTool.Core.Localization;
using eViSTool.Core.Settings;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Два окна eViSTool пишут один settings.json: правку, сделанную в другом окне, это окно не затирает молча — спрашивает,
/// а при закрытии (у него к тому времени не сохранены только размеры) просто не пишет.
/// </summary>
public sealed class SettingsConflictTests : IDisposable
{
    private readonly string? _saved = File.Exists(AppPaths.SettingsFile) ? File.ReadAllText(AppPaths.SettingsFile) : null;

    // настройки тестовой папки данных общие для всех тестов окна — вернуть как было
    public void Dispose()
    {
        if (_saved is null) File.Delete(AppPaths.SettingsFile);
        else File.WriteAllText(AppPaths.SettingsFile, _saved);
    }

    private static MainViewModel Open()
    {
        var language = Loc.Instance.Language; // главная модель выставляет язык из настроек — другим тестам его не менять
        var main = new MainViewModel { AutoCheckUpdates = false };
        Loc.Instance.SetLanguage(language);
        return main;
    }

    /// <summary>Другое окно добавило в настройки отметку (флаг, которого у этого окна нет в памяти).</summary>
    private static void OtherWindowWrites()
    {
        var other = new SettingsStore();
        var theirs = other.Load();
        theirs.CopyWorldsBeforeModChanges = !theirs.CopyWorldsBeforeModChanges;
        other.Save(theirs);
    }

    [Fact]
    public void ChangeAfterAnotherWindowSaved_Yes_WritesOurs() => Sta.Run(() =>
    {
        var main = Open();
        OtherWindowWrites();
        using var dialogs = new ScriptedDialogs(Answer.Yes);

        main.AllowUnstable = !main.AllowUnstable;

        Assert.Equal([Loc.T("settings.changedElsewhere")], dialogs.Asked);
        Assert.Equal(main.AllowUnstable, new SettingsStore().Load().AllowUnstable);
        Assert.Equal(0, dialogs.Restarts);
    });

    [Fact]
    public void ChangeAfterAnotherWindowSaved_No_KeepsTheirs_AndRestartsThisWindow() => Sta.Run(() =>
    {
        var main = Open();
        OtherWindowWrites();
        var theirs = File.ReadAllText(AppPaths.SettingsFile);
        using var dialogs = new ScriptedDialogs(Answer.No);

        main.AllowUnstable = !main.AllowUnstable;
        main.AllowUnstable = !main.AllowUnstable; // дальше это окно уже ничего не пишет и не спрашивает
        main.SaveOnExit();

        Assert.Single(dialogs.Asked);
        Assert.Equal(1, dialogs.Restarts);
        Assert.Equal(theirs, File.ReadAllText(AppPaths.SettingsFile));
    });

    [Fact]
    public void Closing_AfterAnotherWindowSaved_DoesNotOverwrite_AndDoesNotAsk() => Sta.Run(() =>
    {
        var main = Open();
        OtherWindowWrites();
        var theirs = File.ReadAllText(AppPaths.SettingsFile);
        using var dialogs = new ScriptedDialogs(); // любой вопрос уронил бы тест

        main.Layout.Width = 1234;
        main.SaveOnExit();

        Assert.Equal(theirs, File.ReadAllText(AppPaths.SettingsFile));
    });

    [Fact]
    public void WithoutAnotherWindow_SavesAsBefore_WithoutAsking() => Sta.Run(() =>
    {
        var main = Open();
        using var dialogs = new ScriptedDialogs();

        main.AllowUnstable = !main.AllowUnstable;
        main.Layout.Width = 1111;
        main.SaveOnExit();

        var saved = new SettingsStore().Load();
        Assert.Equal(main.AllowUnstable, saved.AllowUnstable);
        Assert.Equal(1111, saved.Layout.Width);
    });
}

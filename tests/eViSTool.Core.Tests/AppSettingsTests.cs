using eViSTool.Core.Settings;

namespace eViSTool.Core.Tests;

/// <summary>settings.json и второе окно eViSTool: правки снаружи замечаются, свои — нет.</summary>
public sealed class AppSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-settings-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File_ => Path.Combine(_dir, "settings.json");

    [Fact]
    public void OwnSaves_AreNotChangesFromElsewhere_ButAnotherWindowsSaveIs()
    {
        var store = new SettingsStore(File_);
        Assert.False(store.ChangedElsewhere); // файла ещё нет

        var settings = store.Load();
        store.Save(settings);
        Assert.False(store.ChangedElsewhere);

        // другое окно (своё хранилище над тем же файлом) записало своё
        var other = new SettingsStore(File_);
        var theirs = other.Load();
        theirs.AutoCheckUpdates = !theirs.AutoCheckUpdates;
        other.Save(theirs);

        Assert.True(store.ChangedElsewhere);
        Assert.False(other.ChangedElsewhere);

        // перечитали — снова в согласии
        store.Load();
        Assert.False(store.ChangedElsewhere);
    }

    [Fact]
    public void FileAppearedAfterAnEmptyStart_IsAChangeFromElsewhere()
    {
        var store = new SettingsStore(File_);
        store.Load(); // файла не было — начали с чистых настроек
        new SettingsStore(File_).Save(new AppSettings());

        Assert.True(store.ChangedElsewhere);
    }
}

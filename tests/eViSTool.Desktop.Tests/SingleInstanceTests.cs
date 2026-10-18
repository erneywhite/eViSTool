using eViSTool.Desktop.Platform;

namespace eViSTool.Desktop.Tests;

/// <summary>Одно окно на папку данных: второй запуск поднимает первое, другая папка живёт рядом, после выхода — снова можно.</summary>
public sealed class SingleInstanceTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-single-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string Data(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    [Fact]
    public async Task SecondStart_FromTheSameFolder_AsksTheFirstToShow_AndExits()
    {
        var data = Data("app");
        using var first = SingleInstance.Start(data, TimeSpan.FromSeconds(1));
        Assert.NotNull(first);
        Assert.True(first.IsOwner);
        var shown = new TaskCompletionSource();
        first.ActivateRequested += () => shown.TrySetResult();

        var second = SingleInstance.Start(data, TimeSpan.FromSeconds(1));

        Assert.Null(second); // этому запуску — выйти
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5)); // а первое окно попросили показаться
    }

    [Fact]
    public void AnotherFolder_IsAnotherInstallation_AndLivesAlongside()
    {
        using var first = SingleInstance.Start(Data("one"), TimeSpan.FromSeconds(1));
        using var other = SingleInstance.Start(Data("two"), TimeSpan.FromSeconds(1));

        Assert.True(first!.IsOwner);
        Assert.True(other!.IsOwner);
    }

    [Fact]
    public void AfterTheFirstExits_TheNextStartOwnsTheFolder()
    {
        var data = Data("app");
        SingleInstance.Start(data, TimeSpan.FromSeconds(1))!.Dispose();

        using var next = SingleInstance.Start(data, TimeSpan.FromSeconds(1));

        Assert.True(next!.IsOwner);
    }

    [Fact]
    public async Task FirstIsClosing_TheNextWaitsForIt_InsteadOfExiting()
    {
        // перезапуск после обновления: новое окно стартует, пока старое ещё держит замок и уже не слушает канал
        var data = Data("app");
        var closing = new FileStream(Path.Combine(data, "window.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _ = Task.Delay(700).ContinueWith(_ => closing.Dispose());

        using var next = await Task.Run(() => SingleInstance.Start(data, TimeSpan.FromSeconds(10)));

        Assert.True(next!.IsOwner);
    }

    [Fact]
    public void PipeName_IsPerFolder()
    {
        Assert.NotEqual(SingleInstance.PipeName(Data("one")), SingleInstance.PipeName(Data("two")));
        Assert.Equal(SingleInstance.PipeName(Data("one")), SingleInstance.PipeName(Data("one") + Path.DirectorySeparatorChar));
    }
}

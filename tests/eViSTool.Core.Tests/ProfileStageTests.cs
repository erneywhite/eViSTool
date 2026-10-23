using eViSTool.Core.Profiles;

namespace eViSTool.Core.Tests;

/// <summary>
/// Папка нового профиля собирается во временной и ставится на место переименованием. На Windows папку, в которой
/// кто-то держит файл открытым, не переименовать — так держит только что скопированное антивирус, и клон профиля с
/// модами и настройками падал с «Access to the path '….evistool-clone-…' is denied». Теперь переименование ждёт.
/// </summary>
public sealed class ProfileStageTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-stage-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Stage_WaitsWhileAFileInsideIsHeldOpen()
    {
        var to = Path.Combine(_root, "Client 3");
        Task? release = null;

        await ProfileCloner.StageAsync(to, stage =>
        {
            var file = Path.Combine(stage, "clientsettings.json");
            File.WriteAllText(file, "{}");
            // «антивирус»: держит файл ещё 600 мс после конца копирования, без права на удаление и переименование
            var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            release = Task.Delay(600).ContinueWith(_ => held.Dispose());
            return Task.CompletedTask;
        });
        await release!;

        Assert.True(File.Exists(Path.Combine(to, "clientsettings.json")));
        Assert.Empty(Directory.GetDirectories(_root, ".*evistool-clone-*"));
    }
}

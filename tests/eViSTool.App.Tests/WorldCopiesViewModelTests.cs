using System.Windows;
using eViSTool.App.ViewModels;
using eViSTool.Core.Profiles;

namespace eViSTool.App.Tests;

/// <summary>«Копии миров» в настройках профиля: список, возврат с вопросом, отказ ничего не трогает.</summary>
public sealed class WorldCopiesViewModelTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "evistool-wcvm-" + Guid.NewGuid().ToString("N"));
    private readonly string _world;
    private readonly GameProfile _profile;

    public WorldCopiesViewModelTests()
    {
        _world = Path.Combine(Directory.CreateDirectory(Path.Combine(_data, "Saves")).FullName, "Home.vcdbs");
        File.WriteAllText(_world, "good");
        WorldCopies.Refresh(_data);
        File.WriteAllText(_world, "broken");
        File.SetLastWriteTimeUtc(_world, DateTime.UtcNow.AddMinutes(5));
        // игры в этой папке нет — профиль «не запущен»
        _profile = new GameProfile { Name = "Solo", Kind = ProfileKind.Client, GameDir = Path.Combine(_data, "nogame"), DataDir = _data };
    }

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ListsTheCopy_AndPutsItBackAfterYes() => Sta.Run(() =>
    {
        using var dialogs = new ScriptedDialogs(MessageBoxResult.Yes);
        var vm = new WorldCopiesViewModel(_profile);
        var row = Assert.Single(vm.Rows);
        Assert.Equal("Home", row.Name);
        Assert.False(vm.IsEmpty);

        vm.RestoreCommand.ExecuteAsync(row).GetAwaiter().GetResult();

        Assert.Contains("Home", Assert.Single(dialogs.Asked));
        Assert.Equal("good", File.ReadAllText(_world));
        // испорченный мир отложен и виден в списке — возврат можно отменить
        Assert.Contains(vm.Rows, r => r.Copy.IsBeforeRestore && File.ReadAllText(r.Copy.CopyPath) == "broken");
    });

    [Fact]
    public void No_ChangesNothing() => Sta.Run(() =>
    {
        using var dialogs = new ScriptedDialogs(MessageBoxResult.No);
        var vm = new WorldCopiesViewModel(_profile);

        vm.RestoreCommand.ExecuteAsync(vm.Rows[0]).GetAwaiter().GetResult();

        Assert.Equal("broken", File.ReadAllText(_world));
        Assert.Single(vm.Rows);
    });
}

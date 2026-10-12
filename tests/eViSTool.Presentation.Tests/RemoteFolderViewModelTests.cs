using System.Net.Http;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Выбор папки на машине с сервером: путь, подпапки, диски, «Вверх», «Выбрать» и ошибки связи. Машина с сервером
/// подменена деревом папок в памяти — Windows с двумя дисками или Linux с одним корнем; сети тесты не трогают.
/// </summary>
public sealed class RemoteFolderViewModelTests
{
    /// <summary>Дерево папок чужой машины: что у кого внутри, с каких корней начинать и что у неё спрашивали.</summary>
    private sealed class Tree
    {
        private readonly Dictionary<string, string[]> _dirs = new(StringComparer.OrdinalIgnoreCase);
        private readonly string[] _roots;
        private readonly string _start;
        private readonly char _sep;

        public Tree(char separator, string start, string[] roots)
        {
            (_sep, _start, _roots) = (separator, start, roots);
        }

        /// <summary>Пути, которые просило окно, по порядку (null и пустая строка — «покажи, с чего начать»).</summary>
        public List<string?> Asked { get; } = [];

        /// <summary>Папки без права чтения: список подпапок пуст не потому, что их нет.</summary>
        public HashSet<string> Denied { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Tree With(string path, params string[] children)
        {
            _dirs[path] = children;
            return this;
        }

        public Task<DirListing> List(string? path)
        {
            Asked.Add(path);
            return Task.FromResult(Resolve(path));
        }

        private DirListing Resolve(string? path)
        {
            var p = string.IsNullOrWhiteSpace(path) ? _start : path.Trim();
            // такой папки нет — ближайшая существующая выше, как у настоящего агента
            string? found;
            while ((found = Find(p)) is null) p = ParentOf(p) ?? _start;
            var denied = Denied.Contains(found);
            return new DirListing(found, ParentOf(found), denied ? [] : _dirs[found], _roots) { Denied = denied };
        }

        // папка с таким путём: без разницы, какого регистра и с какими слэшами на конце набрали; путь в ответе — настоящий
        private string? Find(string path) =>
            _dirs.Keys.FirstOrDefault(k => string.Equals(k.TrimEnd(_sep), path.TrimEnd(_sep), StringComparison.OrdinalIgnoreCase));

        private string? ParentOf(string path)
        {
            if (_roots.Contains(path, StringComparer.OrdinalIgnoreCase)) return null;
            var t = path.TrimEnd(_sep);
            var i = t.LastIndexOf(_sep);
            if (i < 0) return null;
            var parent = t[..(i + 1)];
            return i > 0 && !_roots.Contains(parent, StringComparer.OrdinalIgnoreCase) ? t[..i] : parent;
        }
    }

    /// <summary>Подмена машины с сервером: умеет только отдавать папки (остальное окну выбора не нужно).</summary>
    private sealed class FakeServerData(Func<string?, Task<DirListing>> list, bool isRemote = true) : IServerData
    {
        public bool IsRemote => isRemote;
        public Task<DirListing> ListDirsAsync(string? path, CancellationToken ct = default) => list(path);

        public Task<ServerAutomation> LoadAutomationAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveAutomationAsync(ServerAutomation settings, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BackupEntry>> ListBackupsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BackupEntry> CopyWorldAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RestoreResult> RestoreAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteBackupAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> CheckBackupDirAsync(string? dir, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StatsReport> LoadStatsAsync(StatsPeriod period, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetStatsEnabledAsync(bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearStatsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Windows с дисками C: и D:.</summary>
    private static Tree Windows() => new Tree('\\', "C:\\", ["C:\\", "D:\\"])
        .With("C:\\", "Games", "Users", "Windows")
        .With("C:\\Users", "erney", "Public")
        .With("C:\\Users\\erney", "Backups")
        .With("C:\\Users\\erney\\Backups")
        .With("D:\\", "Games", "Storage")
        .With("D:\\Games");

    /// <summary>Linux: корень один.</summary>
    private static Tree Linux() => new Tree('/', "/", ["/"])
        .With("/", "home", "mnt", "srv")
        .With("/home", "vs")
        .With("/home/vs", "backups", "data")
        .With("/home/vs/backups");

    private static RemoteFolderViewModel Model(Tree tree, string? start, bool remote = true, string heading = "Папка для копий мира") =>
        new(new FakeServerData(tree.List, remote), start, heading);

    private static RemoteFolderViewModel ModelFor(Func<string?, Task<DirListing>> list, bool remote = true, string? start = "C:\\") =>
        new(new FakeServerData(list, remote), start, "Папка");

    [Fact]
    public async Task Load_ShowsTheStartFolder()
    {
        var tree = Windows();
        var vm = Model(tree, "C:\\Users");

        Assert.Equal("Папка для копий мира", vm.Heading);
        Assert.Equal("", vm.PathText);
        Assert.Empty(vm.Dirs);

        await vm.LoadAsync();

        Assert.Equal(["C:\\Users"], tree.Asked);
        Assert.Equal("C:\\Users", vm.PathText);
        Assert.Equal(["erney", "Public"], vm.Dirs);
        Assert.True(vm.CanGoUp);
        Assert.Equal(["C:\\", "D:\\"], vm.Roots);
        Assert.True(vm.HasManyRoots);
        Assert.Equal("C:\\", vm.SelectedRoot);
        Assert.Null(vm.SelectedDir);
        Assert.False(vm.HasError);
        Assert.Equal("", vm.Error);
    }

    [Fact]
    public async Task Load_WithoutStart_AsksTheServerWhereToBegin()
    {
        var tree = Windows();
        var vm = Model(tree, null);

        await vm.LoadAsync();

        Assert.Null(Assert.Single(tree.Asked));
        Assert.Equal("C:\\", vm.PathText);
        Assert.False(vm.HasError); // пути не просили — «нет такой папки» говорить не о чем
    }

    [Fact]
    public async Task Linux_HasOneRoot_SoThereIsNothingToPick()
    {
        var vm = Model(Linux(), "/home/vs");

        await vm.LoadAsync();

        Assert.Equal(["/"], vm.Roots);
        Assert.False(vm.HasManyRoots);
        Assert.Equal("/", vm.SelectedRoot);
        Assert.Equal(["backups", "data"], vm.Dirs);
    }

    [Fact]
    public async Task Root_IsFoundIgnoringCase()
    {
        var tree = new Tree('\\', "C:\\", ["C:\\", "D:\\"]).With("d:\\games", "mods");
        var vm = Model(tree, "d:\\games");

        await vm.LoadAsync();

        Assert.Equal("D:\\", vm.SelectedRoot);
    }

    [Fact]
    public async Task Root_ForPathOnNoDrive_IsNothing()
    {
        // сетевой путь не лежит ни на одном диске из списка: список дисков ничего не выбирает
        var tree = new Tree('\\', "C:\\", ["C:\\", "D:\\"]).With("\\\\nas\\share", "backups");
        var vm = Model(tree, "\\\\nas\\share");

        await vm.LoadAsync();

        Assert.Null(vm.SelectedRoot);
        Assert.Equal(["backups"], vm.Dirs);
    }

    [Theory]
    [InlineData("C:\\Users", "erney", "C:\\Users\\erney")]
    [InlineData("C:\\", "Users", "C:\\Users")]
    [InlineData("/home", "vs", "/home/vs")]
    [InlineData("/", "home", "/home")]
    public async Task Child_UsesTheSeparatorOfTheServersMachine(string open, string name, string expected)
    {
        var tree = open.Contains('\\') ? Windows() : Linux();
        var vm = Model(tree, open);
        await vm.LoadAsync();

        Assert.Equal(expected, vm.Child(name));
    }

    [Fact]
    public void Child_BeforeAnythingIsOpen_IsTheNameItself()
    {
        var vm = Model(Windows(), "C:\\");

        Assert.Equal("Users", vm.Child("Users"));
    }

    [Fact]
    public async Task Open_EntersTheSubfolder()
    {
        var tree = Windows();
        var vm = Model(tree, "C:\\Users");
        await vm.LoadAsync();

        await vm.OpenCommand.ExecuteAsync("erney");

        Assert.Equal(["C:\\Users", "C:\\Users\\erney"], tree.Asked);
        Assert.Equal("C:\\Users\\erney", vm.PathText);
        Assert.Equal(["Backups"], vm.Dirs);
    }

    [Fact]
    public async Task Open_OnLinux_JoinsWithSlash()
    {
        var tree = Linux();
        var vm = Model(tree, "/home");
        await vm.LoadAsync();

        await vm.OpenCommand.ExecuteAsync("vs");

        Assert.Equal("/home/vs", vm.PathText);
        Assert.Equal(["backups", "data"], vm.Dirs);
    }

    [Fact]
    public async Task Open_NeedsAFolder_EnterWithNothingMarkedDoesNothing()
    {
        var vm = Model(Windows(), "C:\\Users");
        await vm.LoadAsync();

        // Enter в списке без отмеченной папки команду не запускает — Enter достаётся кнопке «Выбрать»
        Assert.False(vm.OpenCommand.CanExecute(null));
        Assert.True(vm.OpenCommand.CanExecute("erney"));
    }

    [Fact]
    public async Task Up_GoesToTheParentFolder()
    {
        var tree = Windows();
        var vm = Model(tree, "C:\\Users\\erney");
        await vm.LoadAsync();
        Assert.True(vm.CanGoUp);
        Assert.True(vm.UpCommand.CanExecute(null));

        await vm.UpCommand.ExecuteAsync(null);
        Assert.Equal("C:\\Users", vm.PathText);

        await vm.UpCommand.ExecuteAsync(null);
        Assert.Equal("C:\\", vm.PathText);
        Assert.Equal(["C:\\Users\\erney", "C:\\Users", "C:\\"], tree.Asked);
    }

    [Fact]
    public async Task Up_AtTheTop_IsUnavailable()
    {
        var tree = Windows();
        var vm = Model(tree, "C:\\");
        Assert.False(vm.UpCommand.CanExecute(null)); // до загрузки идти некуда

        await vm.LoadAsync();

        Assert.False(vm.CanGoUp);
        Assert.False(vm.UpCommand.CanExecute(null));
        await vm.UpCommand.ExecuteAsync(null);
        Assert.Equal(["C:\\"], tree.Asked); // лишнего запроса «Вверх» не делает
    }

    [Fact]
    public async Task Up_CommandAvailabilityFollowsTheFolder()
    {
        var vm = Model(Linux(), "/home");
        var seen = new List<bool>(); // что видит кнопка каждый раз, когда её спрашивают заново
        vm.UpCommand.CanExecuteChanged += (_, _) => seen.Add(vm.UpCommand.CanExecute(null));

        await vm.LoadAsync();
        Assert.True(vm.UpCommand.CanExecute(null));
        Assert.True(seen.LastOrDefault());

        await vm.UpCommand.ExecuteAsync(null); // «/» — выше некуда
        Assert.False(vm.UpCommand.CanExecute(null));
        Assert.False(seen.Last());
    }

    [Fact]
    public async Task TypedPath_Enter_GoesThere()
    {
        var tree = Windows();
        var vm = Model(tree, "C:\\Users");
        await vm.LoadAsync();

        vm.PathText = "D:\\Games"; // вписали и нажали Enter
        await vm.GoToPathCommand.ExecuteAsync(null);

        Assert.Equal(["C:\\Users", "D:\\Games"], tree.Asked);
        Assert.Equal("D:\\Games", vm.PathText);
        Assert.False(vm.HasError);
        // диск сменился вместе с папкой, но второго перехода «в корень диска» не было
        Assert.Equal("D:\\", vm.SelectedRoot);
        Assert.Empty(vm.Dirs);
    }

    [Fact]
    public async Task TypedPath_NoSuchFolder_ShowsTheNearestOneAndSaysSo()
    {
        var tree = Windows();
        var vm = Model(tree, "C:\\");
        await vm.LoadAsync();

        vm.PathText = "C:\\Users\\nobody\\Backups";
        await vm.GoToPathCommand.ExecuteAsync(null);

        Assert.Equal("C:\\Users", vm.PathText); // строка пути показывает, где мы на самом деле
        Assert.Equal(["erney", "Public"], vm.Dirs);
        Assert.True(vm.HasError);
        Assert.Equal(Loc.T("rfolder.notFound", "C:\\Users\\nobody\\Backups"), vm.Error);
    }

    [Theory]
    [InlineData("C:\\Users\\")]
    [InlineData("  c:\\USERS  ")]
    [InlineData("c:\\users\\\\")]
    public async Task TypedPath_ThatDiffersOnlyInCaseAndEndingSlashes_IsNotAnError(string typed)
    {
        var vm = Model(Windows(), "C:\\");
        await vm.LoadAsync();

        vm.PathText = typed;
        await vm.GoToPathCommand.ExecuteAsync(null);

        Assert.Equal("C:\\Users", vm.PathText);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task TypedPath_Empty_ShowsTheStartWithoutComplaint()
    {
        var tree = Windows();
        var vm = Model(tree, "C:\\Users");
        await vm.LoadAsync();

        vm.PathText = "";
        await vm.GoToPathCommand.ExecuteAsync(null);

        Assert.Equal("", tree.Asked[^1]);
        Assert.Equal("C:\\", vm.PathText);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task FolderWithoutRights_SaysSo()
    {
        var tree = Windows();
        tree.Denied.Add("C:\\Users");
        var vm = Model(tree, "C:\\Users");

        await vm.LoadAsync();

        Assert.Equal(Loc.T("rfolder.denied"), vm.Error);
        Assert.Equal("C:\\Users", vm.PathText);
        Assert.Empty(vm.Dirs);
    }

    [Fact]
    public async Task Denied_WinsOverNotFound()
    {
        var tree = Windows();
        tree.Denied.Add("C:\\Users");
        var vm = Model(tree, "C:\\Users\\nobody"); // ближайшая выше — закрытая

        await vm.LoadAsync();

        Assert.Equal(Loc.T("rfolder.denied"), vm.Error);
    }

    [Fact]
    public async Task NextSuccessfulMove_ClearsTheError()
    {
        var tree = Windows();
        var vm = Model(tree, "C:\\Users\\nobody");
        await vm.LoadAsync();
        Assert.True(vm.HasError);

        await vm.OpenCommand.ExecuteAsync("erney");

        Assert.False(vm.HasError);
        Assert.Equal("", vm.Error);
    }

    [Fact]
    public async Task NewListing_ResetsTheMarkedFolder()
    {
        var vm = Model(Windows(), "C:\\Users");
        await vm.LoadAsync();
        vm.SelectedDir = "Public";

        await vm.OpenCommand.ExecuteAsync("erney");

        Assert.Null(vm.SelectedDir);
    }

    [Fact]
    public async Task RemoteFailure_IsDescribedInHumanWords()
    {
        var calls = 0;
        Exception[] failures =
        [
            new InvalidOperationException("401 Unauthorized"),
            new InvalidOperationException("429 Too Many Requests"),
            new InvalidOperationException("404 Not Found"),
            new HttpRequestException("нет маршрута"),
            new TaskCanceledException("таймаут"),
        ];
        var vm = ModelFor(_ => Task.FromException<DirListing>(failures[calls++]));
        string[] expected =
        [
            Loc.T("remote.errKey"),
            Loc.T("remote.errTooMany"),
            Loc.T("remote.errOldAgent"),
            Loc.T("remote.errNoConnection", "нет маршрута"),
            Loc.T("remote.errNoConnection", "таймаут"),
        ];

        foreach (var text in expected)
        {
            await vm.GoAsync("C:\\");
            Assert.Equal(text, vm.Error);
            Assert.True(vm.HasError);
        }
    }

    [Fact]
    public async Task LocalFailure_ShowsTheMessageAsIs()
    {
        var vm = ModelFor(_ => Task.FromException<DirListing>(new IOException("диск не готов")), remote: false);

        await vm.LoadAsync();

        Assert.Equal("диск не готов", vm.Error);
    }

    [Fact]
    public async Task Failure_LeavesTheListingAsItWas()
    {
        var fail = false;
        var tree = Windows();
        var vm = ModelFor(path => fail ? Task.FromException<DirListing>(new IOException("сбой")) : tree.List(path), remote: false);
        await vm.GoAsync("C:\\Users");
        vm.SelectedDir = "Public";

        fail = true;
        await vm.OpenCommand.ExecuteAsync("erney");

        Assert.Equal("сбой", vm.Error);
        Assert.Equal("C:\\Users", vm.PathText);
        Assert.Equal(["erney", "Public"], vm.Dirs);
        Assert.Equal("Public", vm.SelectedDir);
        Assert.True(vm.CanGoUp);
        // а выбрать можно по-прежнему ту папку, что открыта
        Assert.Equal("C:\\Users\\Public", Choose(vm));
    }

    [Fact]
    public async Task UnexpectedFailure_IsNotHidden()
    {
        var vm = ModelFor(_ => Task.FromException<DirListing>(new ArgumentException("ошибка в коде")));

        await Assert.ThrowsAsync<ArgumentException>(() => vm.GoAsync("C:\\"));
    }

    [Fact]
    public async Task PickingAnotherDrive_GoesToItsRoot()
    {
        var tree = Windows();
        var vm = Model(tree, "C:\\Users");
        await vm.LoadAsync();

        vm.SelectedRoot = "D:\\";
        await vm.Navigation;

        Assert.Equal(["C:\\Users", "D:\\"], tree.Asked);
        Assert.Equal("D:\\", vm.PathText);
        Assert.Equal(["Games", "Storage"], vm.Dirs);
        Assert.Equal("D:\\", vm.SelectedRoot);
    }

    [Fact]
    public async Task FillingTheDriveList_DoesNotMoveAnywhere()
    {
        var tree = Windows();
        var vm = Model(tree, "C:\\Users");
        await vm.LoadAsync();
        await vm.OpenCommand.ExecuteAsync("erney");

        // диск, на котором уже стоим, и пустое значение (список дисков обновился) переходом не считаются
        vm.SelectedRoot = null;
        vm.SelectedRoot = "C:\\";
        await vm.Navigation;

        Assert.Equal(["C:\\Users", "C:\\Users\\erney"], tree.Asked);
        Assert.Equal("C:\\Users\\erney", vm.PathText);
    }

    [Fact]
    public async Task DriveList_ReportsChangesToTheView()
    {
        var vm = Model(Windows(), "C:\\");
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.LoadAsync();

        Assert.Contains(nameof(RemoteFolderViewModel.Roots), changed);
        Assert.Contains(nameof(RemoteFolderViewModel.HasManyRoots), changed);
        Assert.Contains(nameof(RemoteFolderViewModel.SelectedRoot), changed);
        Assert.Contains(nameof(RemoteFolderViewModel.PathText), changed);
        Assert.Contains(nameof(RemoteFolderViewModel.Dirs), changed);
    }

    [Fact]
    public async Task ErrorText_ReportsVisibilityToTheView()
    {
        var vm = Model(Windows(), "C:\\Users\\nobody");
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.LoadAsync();

        Assert.Contains(nameof(RemoteFolderViewModel.HasError), changed);
    }

    [Fact]
    public async Task LateAnswerToAnEarlierMove_IsIgnored()
    {
        // агент по сети ответил на первый переход позже, чем на второй: список не должен «откатиться»
        var slow = new TaskCompletionSource<DirListing>();
        var tree = Windows();
        var vm = ModelFor(path => path == "C:\\Users" ? slow.Task : tree.List(path));

        var first = vm.GoAsync("C:\\Users");
        await vm.GoAsync("D:\\Games");
        slow.SetResult(new DirListing("C:\\Users", "C:\\", ["erney"], ["C:\\", "D:\\"]));
        await first;

        Assert.Equal("D:\\Games", vm.PathText);
        Assert.Empty(vm.Dirs);
        Assert.Equal("D:\\", vm.SelectedRoot);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task LateFailureOfAnEarlierMove_IsIgnored()
    {
        var slow = new TaskCompletionSource<DirListing>();
        var tree = Windows();
        var vm = ModelFor(path => path == "C:\\Users" ? slow.Task : tree.List(path), remote: false);

        var first = vm.GoAsync("C:\\Users");
        await vm.GoAsync("D:\\Games");
        slow.SetException(new IOException("сбой"));
        await first;

        Assert.Equal("D:\\Games", vm.PathText);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task Choose_TakesTheMarkedSubfolder()
    {
        var vm = Model(Windows(), "C:\\Users");
        await vm.LoadAsync();
        vm.SelectedDir = "erney";

        Assert.Equal("C:\\Users\\erney", Choose(vm));
        Assert.Equal("C:\\Users\\erney", vm.Selected);
    }

    [Fact]
    public async Task Choose_WithNothingMarked_TakesTheOpenFolder()
    {
        var vm = Model(Linux(), "/home/vs");
        await vm.LoadAsync();

        Assert.Equal("/home/vs", Choose(vm));
    }

    [Fact]
    public async Task Choose_TakesTheOpenFolder_NotWhatIsTypedButNotEntered()
    {
        var vm = Model(Windows(), "C:\\Users");
        await vm.LoadAsync();

        vm.PathText = "D:\\Storage"; // вписали, но Enter не нажали

        Assert.Equal("C:\\Users", Choose(vm));
    }

    [Fact]
    public void Choose_BeforeAnythingIsOpen_DoesNothing()
    {
        var vm = Model(Windows(), "C:\\");
        var chosen = new List<string>();
        vm.Chosen += chosen.Add;

        vm.ChooseCommand.Execute(null);

        Assert.Empty(chosen);
        Assert.Null(vm.Selected);
    }

    /// <summary>Нажать «Выбрать» и вернуть то, что получило окно.</summary>
    private static string Choose(RemoteFolderViewModel vm)
    {
        var chosen = new List<string>();
        vm.Chosen += chosen.Add;
        vm.ChooseCommand.Execute(null);
        return Assert.Single(chosen);
    }
}

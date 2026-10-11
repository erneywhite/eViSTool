using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Выбор папки на машине с сервером (другой компьютер, в том числе Linux): стандартный диалог Windows показывает только
/// эту машину, поэтому папки берутся у агента через <see cref="IServerData.ListDirsAsync"/>. Только папки, без файлов.
/// Окно показывает путь (его можно вписать и нажать Enter), «Вверх», диски (если их несколько) и подпапки; «Выбрать»
/// берёт отмеченную подпапку, иначе ту, где стоим, и окно получает ответ через <see cref="Chosen"/>.
/// </summary>
public sealed partial class RemoteFolderViewModel : ObservableObject
{
    private readonly IServerData _data;
    private readonly string? _start;
    private DirListing? _current;
    // номер последнего перехода: ответ более раннего, пришедший позже (агент по сети отвечает не по порядку), не показываем
    private int _generation;
    private string _pathText = "";
    private string? _selectedDir;
    private bool _canGoUp;
    private IReadOnlyList<string> _dirs = [];
    private IReadOnlyList<string> _roots = [];
    private string? _selectedRoot;
    private string _error = "";

    /// <param name="data">Откуда брать папки: агент удалённого сервера (в тестах — подмена).</param>
    /// <param name="start">С какого пути начать; пусто — с того, что предложит машина с сервером.</param>
    /// <param name="heading">Крупная надпись в окне: что за папку выбирают.</param>
    public RemoteFolderViewModel(IServerData data, string? start, string heading)
    {
        _data = data;
        _start = start;
        Heading = heading;
    }

    public string Heading { get; }

    /// <summary>Выбранная папка (полный путь на машине с сервером); null — ещё не выбирали.</summary>
    public string? Selected { get; private set; }

    /// <summary>Последний начатый переход (тесты ждут его, когда переход запустила привязка, а не команда).</summary>
    public Task Navigation { get; private set; } = Task.CompletedTask;

    /// <summary>Строка пути: показывает, где стоим; в неё можно вписать другой путь и нажать Enter.</summary>
    public string PathText
    {
        get => _pathText;
        set => SetProperty(ref _pathText, value);
    }

    /// <summary>Подпапки открытой папки (только имена).</summary>
    public IReadOnlyList<string> Dirs
    {
        get => _dirs;
        private set => SetProperty(ref _dirs, value);
    }

    /// <summary>Отмеченная подпапка; null — ничего не отмечено (список обновился — отметка сбрасывается).</summary>
    public string? SelectedDir
    {
        get => _selectedDir;
        set => SetProperty(ref _selectedDir, value);
    }

    /// <summary>Есть папка выше — «Вверх» доступна.</summary>
    public bool CanGoUp
    {
        get => _canGoUp;
        private set
        {
            if (SetProperty(ref _canGoUp, value)) UpCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>С чего можно начать: на Windows — диски, на Linux — корень.</summary>
    public IReadOnlyList<string> Roots
    {
        get => _roots;
        private set
        {
            if (SetProperty(ref _roots, value)) OnPropertyChanged(nameof(HasManyRoots));
        }
    }

    /// <summary>Дисков несколько — список дисков виден (на Linux корень один, выбирать нечего).</summary>
    public bool HasManyRoots => Roots.Count > 1;

    /// <summary>
    /// Диск, под которым открытая папка. Выбрали другой — переход в его корень. Когда диск ставит сам список после
    /// перехода, открытая папка уже на нём, и переходить некуда: отличить выбор от заполнения позволяет эта проверка,
    /// а не отдельный флаг.
    /// </summary>
    public string? SelectedRoot
    {
        get => _selectedRoot;
        set
        {
            if (!SetProperty(ref _selectedRoot, value)) return;
            if (value is null || _current is null || IsUnder(_current.Path, value)) return;
            _ = GoAsync(value);
        }
    }

    /// <summary>Что не так: нет связи, нет прав, такой папки там нет. Пусто — всё хорошо.</summary>
    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => Error.Length > 0;

    /// <summary>Папка выбрана — окно закрывается с этим путём.</summary>
    public event Action<string>? Chosen;

    /// <summary>Открыть стартовую папку: окно показано.</summary>
    public Task LoadAsync() => GoAsync(_start);

    /// <summary>
    /// Показать папку path (пусто — стартовую у машины с сервером). Такой папки нет — машина покажет ближайшую выше, и
    /// это видно из сообщения. Ожидаемые сбои связи и прав — в <see cref="Error"/>, список остаётся прежним.
    /// </summary>
    public Task GoAsync(string? path) => Navigation = GoCoreAsync(path);

    private async Task GoCoreAsync(string? path)
    {
        var generation = ++_generation;
        Error = "";
        try
        {
            var listing = await _data.ListDirsAsync(path);
            if (generation != _generation) return;
            Apply(listing);
            if (listing.Denied) Error = Loc.T("rfolder.denied");
            else if (path is { Length: > 0 } asked && !SamePath(asked, listing.Path)) Error = Loc.T("rfolder.notFound", asked);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or IOException)
        {
            if (generation != _generation) return;
            Error = _data.IsRemote ? RemoteSecret.Describe(ex) : ex.Message;
        }
    }

    private void Apply(DirListing listing)
    {
        // открытая папка запоминается до выбора диска: выбор «этого же диска» переходом не считается (см. SelectedRoot)
        _current = listing;
        PathText = listing.Path;
        Dirs = listing.Dirs;
        SelectedDir = null;
        CanGoUp = listing.Parent is not null;
        Roots = listing.Roots;
        SelectedRoot = listing.Roots.FirstOrDefault(r => IsUnder(listing.Path, r));
    }

    private static bool IsUnder(string path, string root) => path.StartsWith(root, StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Путь подпапки name открытой папки. Собираем разделителем машины с сервером, а не этой: на Linux — «/», на Windows —
    /// «\». Папка ещё не открыта — само имя.
    /// </summary>
    public string Child(string name) =>
        _current is not { } c ? name
            : c.Path.EndsWith('/') || c.Path.EndsWith('\\') ? c.Path + name
            : c.Path + (c.Path.Contains('/') && !c.Path.Contains('\\') ? "/" : "\\") + name;

    /// <summary>Enter в строке пути: перейти туда, что вписано.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task GoToPathAsync() => GoAsync(PathText);

    /// <summary>«Вверх».</summary>
    [RelayCommand(CanExecute = nameof(CanGoUp), AllowConcurrentExecutions = true)]
    private Task UpAsync() => _current?.Parent is { } parent ? GoAsync(parent) : Task.CompletedTask;

    /// <summary>Войти в подпапку: двойной щелчок по ней или Enter на отмеченной. Подпапки нет (null) — команда недоступна.</summary>
    [RelayCommand(CanExecute = nameof(CanOpen), AllowConcurrentExecutions = true)]
    private Task OpenAsync(string? name) => GoAsync(Child(name!));

    private static bool CanOpen(string? name) => name is not null;

    /// <summary>«Выбрать»: отмеченную подпапку, иначе ту, где стоим. Пока ничего не открыто — нечего выбирать.</summary>
    [RelayCommand]
    private void Choose()
    {
        if (_current is null) return;
        Selected = SelectedDir is { } name ? Child(name) : _current.Path;
        Chosen?.Invoke(Selected);
    }
}

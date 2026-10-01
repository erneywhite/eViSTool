using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Game;
using eViSTool.Core.ModDb;
using eViSTool.Core.Profiles;
using eViSTool.Core.Settings;
using eViSTool.Core.Localization;

namespace eViSTool.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsStore _store = new();
    private readonly AppSettings _settings;

    public ModsViewModel Mods { get; }
    public CatalogViewModel Catalog { get; }
    public AboutViewModel About { get; } = new();
    public ServerViewModel Server { get; }

    /// <summary>Открытая вкладка: 0 — моды, 1 — каталог, …</summary>
    [ObservableProperty] private int _selectedTab;

    /// <summary>Название открытого раздела — в полосе сверху.</summary>
    public string PageTitle => Loc.T(SelectedTab switch
    {
        1 => "nav.catalog",
        2 => "nav.server",
        3 => "nav.settings",
        4 => "nav.about",
        _ => "nav.mods",
    });

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(PageTitle));
        Mods.SetActive(value == 0);
        if (value == 1) _ = Catalog.EnsureLoadedAsync(); // каталог грузим только когда он нужен
    }
    public ObservableCollection<ProfileViewModel> Profiles { get; } = [];

    /// <summary>Профиль, с которым сейчас работаем (переключатель в шапке).</summary>
    [ObservableProperty] private ProfileViewModel? _activeProfile;

    /// <summary>Профиль, открытый в редакторе на вкладке «Настройки».</summary>
    [ObservableProperty] private ProfileViewModel? _editedProfile;

    // ---- запуск игры с активным клиентским профилем

    /// <summary>Кнопка «Играть» — только для клиентского профиля (у серверного свой раздел «Сервер»).</summary>
    public bool CanPlay => ActiveProfile is { Kind: ProfileKind.Client };

    [ObservableProperty] private bool _isLaunching;

    public string PlayText => Loc.T(IsLaunching ? "play.starting" : "play.button");

    public string PlayTip => ActiveProfile is not { Kind: ProfileKind.Client } profile ? ""
        : GameLauncher.DataPathFor(profile.Model) is { } dataPath ? Loc.T("play.tipOwnData", profile.Name, dataPath)
        : Loc.T("play.tip", profile.Name);

    partial void OnIsLaunchingChanged(bool value) => OnPropertyChanged(nameof(PlayText));

    private void NotifyPlay()
    {
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(PlayText));
        OnPropertyChanged(nameof(PlayTip));
    }

    [RelayCommand]
    private async Task Play()
    {
        if (ActiveProfile is not { Kind: ProfileKind.Client } profile) return;
        var owner = System.Windows.Application.Current.MainWindow!;

        // вторая копия игры с той же папкой данных перезапишет настройки первой — спрашиваем
        if (GameProcess.IsRunning(profile.Model)
            && System.Windows.MessageBox.Show(owner, Loc.T("play.alreadyRunning"), "eViSTool", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.No) != System.Windows.MessageBoxResult.Yes)
            return;

        try
        {
            GameLauncher.Launch(profile.Model);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            System.Windows.MessageBox.Show(owner, Loc.T("play.failed", ex.Message), "eViSTool",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        // окно игры появляется не сразу — пока не даём нажать второй раз
        IsLaunching = true;
        try { await Task.Delay(TimeSpan.FromSeconds(8)); }
        finally { IsLaunching = false; }
    }

    [ObservableProperty] private bool _allowUnstable;
    [ObservableProperty] private bool _autoCheckUpdates;

    public IReadOnlyList<Choice<string>> Languages { get; } =
        Loc.Available.Select(l => new Choice<string>(l.Name, l.Code)).ToList();

    [ObservableProperty] private Choice<string> _selectedLanguage = null!;

    partial void OnSelectedLanguageChanged(Choice<string> value)
    {
        if (value.Value == Loc.Instance.Language) return;
        _settings.Language = value.Value;
        Save();
        Loc.Instance.SetLanguage(value.Value);

        // подписи в разметке обновились сами; тексты, собранные в коде, пересобираем
        OnPropertyChanged(nameof(DataLocationText));
        OnPropertyChanged(nameof(PageTitle));
        foreach (var p in Profiles) p.NotifyLanguageChanged();
        NotifyPlay();
        Mods.OnProfileSwitched();
        Catalog.OnLanguageChanged();
        Server.OnLanguageChanged();
    }

    partial void OnAutoCheckUpdatesChanged(bool value)
    {
        _settings.AutoCheckUpdates = value;
        Save();
    }

    public string DataLocationText => Core.AppPaths.IsPortable
        ? Loc.T("settings.dataPortable", Core.AppPaths.Root)
        : Loc.T("settings.dataFallback", Core.AppPaths.Root);

    [RelayCommand]
    private void OpenDataFolder() => Shell.OpenFolder(Core.AppPaths.Root);

    public MainViewModel()
    {
        _settings = _store.Load();
        Loc.Instance.SetLanguage(_settings.Language); // до того, как VM начнут собирать тексты
        foreach (var p in _settings.Profiles) Profiles.Add(new ProfileViewModel(p, OnProfileChanged));

        _allowUnstable = _settings.AllowUnstable;
        _autoCheckUpdates = _settings.AutoCheckUpdates;
        _selectedLanguage = Languages.FirstOrDefault(l => l.Value == Loc.Instance.Language) ?? Languages[0];
        _activeProfile = Profiles.FirstOrDefault(p => p.Model == _settings.ActiveProfile);
        _editedProfile = _activeProfile;

        var db = new ModDbClient();
        Mods = new ModsViewModel(this, db);
        Catalog = new CatalogViewModel(this, db);
        Server = new ServerViewModel(this);
        About.Profiles = () => _settings.Profiles;
        Server.OnProfileSwitched();
        Save(); // перенос настроек старого формата сразу на диск
        Mods.OnProfileSwitched(); // список модов виден сразу, без сети
        Mods.SetActive(SelectedTab == 0); // и сам обновляется, если моды поменяли в другом окне
        _ = About.CheckQuietlyAsync(); // новая версия программы — подсказка в боковой панели

        // Удалённый доступ работает, пока открыто это окно (или пока работает сервер): держим агентов таких профилей
        // запущенными. Закрыли окно и сервер не работает — агент через пару минут выйдет, в системе ничего не остаётся.
        _keepRemote = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _keepRemote.Tick += (_, _) => _ = KeepRemoteAgentsAsync();
        _keepRemote.Start();
        _ = KeepRemoteAgentsAsync();
    }

    private readonly System.Windows.Threading.DispatcherTimer _keepRemote;

    private async Task KeepRemoteAgentsAsync()
    {
        foreach (var profile in Profiles.Where(p => p.Kind == ProfileKind.Server).Select(p => p.Model).ToList())
        {
            if (!Core.Server.Remote.RemoteAccess.Load(profile.Id).Enabled) continue;
            try
            {
                using var client = await Core.Server.AgentLauncher.EnsureRunningAsync(profile, startServer: false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException
                                           or System.Net.Http.HttpRequestException or System.ComponentModel.Win32Exception)
            {
                // папки игры нет и т. п. — вкладка «Удалённый доступ» покажет, что агент не запущен
            }
        }
    }

    [RelayCommand]
    private void ShowAbout() => SelectedTab = 4;

    partial void OnActiveProfileChanged(ProfileViewModel? value)
    {
        _settings.ActiveProfileId = value?.Model.Id;
        Save();
        NotifyPlay();
        Mods.OnProfileSwitched();
        Server.OnProfileSwitched();
    }

    partial void OnAllowUnstableChanged(bool value)
    {
        _settings.AllowUnstable = value;
        Save();
        Mods.ReloadLocal();
    }

    private void OnProfileChanged(ProfileViewModel profile)
    {
        Save();
        if (profile == ActiveProfile)
        {
            NotifyPlay(); // имя, тип или папка данных профиля — в кнопке «Играть» и её подсказке
            Mods.OnProfileSwitched();
            Server.OnProfileSwitched();
        }
    }

    public WindowLayout Layout => _settings.Layout;

    /// <summary>Перейти на вкладку «Каталог» и открыть там мод.</summary>
    public async Task ShowInCatalogAsync(long? assetId, string? modId, string? name)
    {
        SelectedTab = 1;
        await Catalog.ShowModAsync(assetId, modId, name);
    }

    /// <summary>Сохранить настройки (например, после закрепления версии мода).</summary>
    public void SaveSettings() => Save();

    private void Save()
    {
        try { _store.Save(_settings); }
        catch (IOException) { /* не критично: попробуем при следующем изменении */ }
    }

    [RelayCommand]
    private void AddClientProfile()
    {
        // новый профиль — со своей папкой данных (…\ClientProfiles\имя) на основе существующего клиентского:
        // текущего, если он клиентский, иначе первого; нет ни одного — на основе стандартной папки игры
        var source = (ActiveProfile is { Kind: ProfileKind.Client } ? ActiveProfile : Profiles.FirstOrDefault(p => p.Kind == ProfileKind.Client))?.Model
                     ?? new GameProfile
                     {
                         Name = Loc.T("profile.defaultClientName"),
                         Kind = ProfileKind.Client,
                         GameDir = KnownGameDir(),
                         DataDir = GameInstall.DefaultDataDir,
                     };
        var dlg = new ClientProfileWindow(source, clone: false, Loc.T("profile.newClient", Profiles.Count + 1))
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (dlg.ShowDialog() == true && dlg.Result is { } created) AddProfile(created);
    }

    [RelayCommand]
    private void AddServerProfile()
    {
        if (AskNewProfile(ProfileKind.Server, Loc.T("profile.newServer", Profiles.Count + 1)) is not { DataDir: { } dataDir } dlg) return;

        // сервер на другом компьютере: профиль без папок, код подключения — зашифрованным
        if (dlg.Existing == NewProfileWindow.ExistingMode.Remote && dlg.RemoteCode is { } code)
        {
            AddProfile(new GameProfile { Name = dlg.ProfileName, Kind = ProfileKind.Server, RemoteCode = Core.Server.Remote.RemoteSecret.Protect(code) });
            return;
        }

        // данные сервера уже есть: взять папку как есть или скопировать её в профиль (как клон: мир, конфиги, данные модов и игроков)
        if (dlg.Existing == NewProfileWindow.ExistingMode.UseInPlace && dlg.ExistingDir is { } inPlace)
        {
            AddProfile(new GameProfile { Name = dlg.ProfileName, Kind = ProfileKind.Server, GameDir = KnownGameDir(), DataDir = inPlace });
            return;
        }
        if (dlg.Existing == NewProfileWindow.ExistingMode.Import && dlg.ExistingDir is { } from)
        {
            var source = new GameProfile { Name = Path.GetFileName(from.TrimEnd('\\', '/')), Kind = ProfileKind.Server, GameDir = KnownGameDir(), DataDir = from };
            // сервер из этой папки игры работает — копия мира может получиться битой, окно не даст начать
            var running = GameProcess.FindServerPids(source).Count > 0;
            var clone = new CloneProfileWindow(source, running, dlg.ProfileName) { Owner = System.Windows.Application.Current.MainWindow };
            if (clone.ShowDialog() == true && clone.Result is { } imported) AddProfile(imported);
            return;
        }

        try
        {
            // своя папка данных внутри VintagestoryData\ServerProfiles, названная как профиль: мир, конфиг и бэкапы — свои,
            // моды — общие. Создаём сразу: профиль с несуществующей папкой выглядел бы сломанным («папка данных не найдена»)
            Directory.CreateDirectory(dataDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // не вышло — её создаст «Создать конфиг» или первый запуск сервера
        }

        AddProfile(new GameProfile { Name = dlg.ProfileName, Kind = ProfileKind.Server, GameDir = KnownGameDir(), DataDir = dataDir });
    }

    /// <summary>Спросить название нового профиля (у серверного от него зависит имя папки данных). null — отменили.</summary>
    private static NewProfileWindow? AskNewProfile(ProfileKind kind, string suggestedName)
    {
        var dlg = new NewProfileWindow(kind, suggestedName) { Owner = System.Windows.Application.Current.MainWindow };
        return dlg.ShowDialog() == true ? dlg : null;
    }

    /// <summary>Папка игры для нового профиля: как у текущего; нет — как у любого другого; нет и там — ищем установку.</summary>
    private string? KnownGameDir() =>
        new[] { ActiveProfile?.GameDir }.Concat(Profiles.Select(p => p.GameDir)).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d))
        ?? GameInstall.FindGameDir();

    private void AddProfile(GameProfile model)
    {
        _settings.Profiles.Add(model);
        var vm = new ProfileViewModel(model, OnProfileChanged);
        Profiles.Add(vm);
        EditedProfile = vm;
        Save();
    }

    /// <summary>
    /// Клон профиля со своей папкой данных: серверного — «тот же мир» или «новый мир»,
    /// клиентского — с общими или своими модами, с настройками и мирами по выбору.
    /// </summary>
    [RelayCommand]
    private async Task CloneProfile(ProfileViewModel? profile)
    {
        if (profile is null || profile.Model.IsRemote) return; // удалённый сервер клонируется на своём компьютере
        if (profile.Kind == ProfileKind.Client)
        {
            var window = new ClientProfileWindow(profile.Model, clone: true, Loc.T("clone.copySuffix", profile.Name))
            {
                Owner = System.Windows.Application.Current.MainWindow,
            };
            if (window.ShowDialog() == true && window.Result is { } copy) AddProfile(copy);
            return;
        }

        var running = await IsServerRunningAsync(profile.Model);
        var dlg = new CloneProfileWindow(profile.Model, running) { Owner = System.Windows.Application.Current.MainWindow };
        if (dlg.ShowDialog() == true && dlg.Result is { } clone) AddProfile(clone);
    }

    private static bool SameOrInside(string? path, string dir)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = Path.GetFullPath(path).TrimEnd('\\', '/');
        var d = Path.GetFullPath(dir).TrimEnd('\\', '/');
        return p.Equals(d, StringComparison.OrdinalIgnoreCase) || p.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static long FolderSize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Работает ли сервер профиля под нашим агентом (копировать мир работающего сервера нельзя).</summary>
    private static async Task<bool> IsServerRunningAsync(GameProfile profile)
    {
        using var client = Core.Server.AgentClient.TryConnect(profile.Id);
        if (client is null) return false;
        try
        {
            return (await client.StatusAsync()).State != Core.Server.ServerState.Stopped;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return false; // агент не отвечает — сервера под ним нет
        }
    }

    [RelayCommand]
    private async Task RemoveProfile(ProfileViewModel? profile)
    {
        if (profile is null || Profiles.Count <= 1) return; // последний профиль не удаляем
        var model = profile.Model;
        var owner = System.Windows.Application.Current.MainWindow!;

        if (model.Kind == ProfileKind.Server && await IsServerRunningAsync(model))
        {
            System.Windows.MessageBox.Show(owner, Loc.T("settings.removeRunning", model.Name), "eViSTool",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        // Папку данных предлагаем удалить только там, где её завела сама программа (…\ServerProfiles\имя, …\ClientProfiles\имя)
        // и где она не нужна другому профилю. Чужие папки (VintagestoryData клиента и т. п.) не трогаем никогда.
        var dir = model.DataDir;
        var ownFolder = !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)
                        && (model.Kind == ProfileKind.Server ? ServerProfileLayout.IsInContainer(dir) : ClientProfileLayout.IsInContainer(dir))
                        && !Profiles.Any(p => p != profile && SameOrInside(p.DataDir, dir));
        var deleteData = false;
        if (ownFolder)
        {
            var size = await Task.Run(() => FolderSize(dir!));
            // по умолчанию — «Нет»: случайный Enter не должен уносить мир
            var answer = System.Windows.MessageBox.Show(owner, Loc.T("settings.removeAskData", model.Name, dir, Sizes.Format(size)), "eViSTool",
                System.Windows.MessageBoxButton.YesNoCancel, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);
            if (answer == System.Windows.MessageBoxResult.Cancel) return;
            deleteData = answer == System.Windows.MessageBoxResult.Yes;
        }
        else if (System.Windows.MessageBox.Show(owner, Loc.T("settings.removeAsk", model.Name, string.IsNullOrWhiteSpace(dir) ? "—" : dir), "eViSTool",
                     System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.No)
                 != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        if (deleteData)
        {
            try
            {
                Shell.MoveToRecycleBin(dir!); // в Корзину, не насовсем: мир можно вернуть
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // папку убрать не удалось — профиль оставляем, чтобы можно было повторить
                System.Windows.MessageBox.Show(owner, Loc.T("settings.removeDataFailed", dir, ex.Message), "eViSTool",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
        }

        // ключ, адрес агента и расписание этого профиля больше не нужны
        foreach (var file in new[] { Core.Server.AgentProtocol.StateFile(model.Id), Core.Server.AgentProtocol.KeyFile(model.Id),
                     Core.Server.ServerAutomation.FileFor(model.Id) })
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        _settings.Profiles.Remove(profile.Model);
        Profiles.Remove(profile);
        if (ActiveProfile == profile) ActiveProfile = Profiles[0];
        if (EditedProfile == profile) EditedProfile = ActiveProfile;
        Save();
    }
}

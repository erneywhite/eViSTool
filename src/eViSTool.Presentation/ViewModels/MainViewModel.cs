using eViSTool.Presentation;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core;
using eViSTool.Core.Game;
using eViSTool.Core.ModDb;
using eViSTool.Core.Profiles;
using eViSTool.Core.Settings;
using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsStore _store = new();
    private readonly AppSettings _settings;

    public ModsViewModel Mods { get; }
    public CatalogViewModel Catalog { get; }
    public AboutViewModel About { get; } = new();
    public ServerViewModel Server { get; }

    /// <summary>Открытая вкладка — номер из <see cref="AppTab"/> (порядок как в боковой панели).</summary>
    [ObservableProperty] private int _selectedTab;

    /// <summary>Название открытого раздела — в полосе сверху.</summary>
    public string PageTitle => Loc.T(SelectedTab switch
    {
        AppTab.Catalog => "nav.catalog",
        AppTab.ModConfig => "nav.modConfig",
        AppTab.Server => "nav.server",
        AppTab.Settings => "nav.settings",
        AppTab.About => "nav.about",
        _ => "nav.mods",
    });

    /// <summary>Вкладка «Настройки модов»: конфиги модов активного профиля.</summary>
    public ModConfigViewModel ModConfig { get; }

    /// <summary>Открыть «Настройки модов» на файлах этого мода (из карточки в «Моих модах»).</summary>
    public void OpenModConfig(string modId, string name)
    {
        ModConfig.Focus(modId, name);
        if (SelectedTab == AppTab.ModConfig) _ = ModConfig.LoadAsync();
        else SelectedTab = AppTab.ModConfig; // перечитает при открытии
    }

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(PageTitle));
        Mods.SetActive(value == AppTab.Mods);
        if (value == AppTab.Catalog) _ = Catalog.EnsureLoadedAsync(); // каталог грузим только когда он нужен
        if (value == AppTab.ModConfig) _ = ModConfig.LoadAsync();    // конфиги — тоже, и свежие при каждом заходе
    }
    public ObservableCollection<ProfileViewModel> Profiles { get; } = [];

    /// <summary>Профиль, с которым сейчас работаем (переключатель в шапке).</summary>
    private ProfileViewModel? _activeProfile;

    /// <summary>
    /// Активный профиль. Перед сменой — правки конфига сервера: «Сохранить? Да / Нет / Отмена»; отмена или неудачное
    /// сохранение оставляют прежний профиль (и список выбора возвращается на него).
    /// </summary>
    public ProfileViewModel? ActiveProfile
    {
        get => _activeProfile;
        set
        {
            if (value == _activeProfile) return;
            if ((Server is not null && !Server.Config.ConfirmSwitch()) || (ModConfig is not null && !ModConfig.ConfirmLeave()))
            {
                ProfileSwitchDeclined?.Invoke(); // список уже показывает новый выбор — окно вернёт его на прежний
                return;
            }
            if (SetProperty(ref _activeProfile, value)) OnActiveProfileChanged(value);
        }
    }

    /// <summary>Профиль, открытый в редакторе на вкладке «Настройки».</summary>
    [ObservableProperty] private ProfileViewModel? _editedProfile;

    /// <summary>«Установить также в» без вопроса — сразу в связанные профили.</summary>
    [ObservableProperty] private bool _alsoInstallWithoutAsking;

    /// <summary>Копия одиночных миров перед изменением модов клиентского профиля.</summary>
    [ObservableProperty] private bool _copyWorldsBeforeModChanges;

    /// <summary>Связи редактируемого профиля: в какие профили ставить моды заодно.</summary>
    public System.Collections.ObjectModel.ObservableCollection<LinkChoice> LinkChoices { get; } = [];

    public bool HasLinkChoices => LinkChoices.Count > 0;

    /// <summary>Пересобрать список связей (сменили редактируемый профиль, добавили/удалили профиль, запомнили выбор).</summary>
    public void RefreshLinks()
    {
        LinkChoices.Clear();
        if (EditedProfile is { } edited)
            foreach (var other in Profiles.Where(p => p != edited))
                LinkChoices.Add(new LinkChoice(other.Name, other.KindText, edited.Model.IsLinkedTo(other.Model), linked =>
                {
                    edited.Model.SetLinked(other.Model, linked);
                    Save();
                }));
        OnPropertyChanged(nameof(HasLinkChoices));
    }

    /// <summary>Копии одиночных миров редактируемого профиля (только у игрового профиля).</summary>
    [ObservableProperty] private WorldCopiesViewModel? _worldCopies;

    /// <summary>Каналы оповещений этого компьютера — карточка в «Настройках».</summary>
    public NotifyChannelsViewModel Notify { get; } = new();

    partial void OnEditedProfileChanged(ProfileViewModel? value)
    {
        NotifyEditedDefaultServer();
        RefreshLinks();
        CleanupDiskCommand.NotifyCanExecuteChanged();
        WorldCopies = value?.Model.Kind == ProfileKind.Client ? new WorldCopiesViewModel(value.Model) : null;
    }

    /// <summary>«Уборка диска» профиля (файлы на этой машине; у удалённого сервера — пока нет).</summary>
    [RelayCommand(CanExecute = nameof(CanCleanupDisk))]
    private void CleanupDisk(ProfileViewModel? profile)
    {
        if (profile is null) return;
        AppWindows.Current.ShowCleanup(profile.Model);
        WorldCopies?.Refresh();
    }

    private static bool CanCleanupDisk(ProfileViewModel? profile) => profile is { Model.IsRemote: false };

    /// <summary>Сделаны новые копии миров — обновить список в настройках, если открыт этот профиль.</summary>
    public void RefreshWorldCopies(GameProfile profile)
    {
        if (EditedProfile?.Model == profile) WorldCopies?.Refresh();
    }

    partial void OnAlsoInstallWithoutAskingChanged(bool value)
    {
        _settings.AlsoInstallWithoutAsking = value;
        Save();
    }

    partial void OnCopyWorldsBeforeModChangesChanged(bool value)
    {
        _settings.CopyWorldsBeforeModChanges = value;
        Save();
    }

    // ---- запуск игры с активным клиентским профилем

    /// <summary>Кнопка «Играть» — только для клиентского профиля (у серверного свой раздел «Сервер»).</summary>
    public bool CanPlay => ActiveProfile is { Kind: ProfileKind.Client };

    [ObservableProperty] private bool _isLaunching;

    public string PlayText => IsGameRunning ? Loc.T("play.running") : IsLaunching ? Loc.T("play.starting") : Loc.T("play.button") + PlaySuffix;

    /// <summary>Игра этого профиля запущена (нами или как угодно ещё: ярлык, лаунчер) — второй экземпляр не даём.</summary>
    [ObservableProperty] private bool _isGameRunning;

    partial void OnIsGameRunningChanged(bool value)
    {
        if (!value) ShowUpdatePrompt(); // окно об обновлении ждало, пока закроют игру
        OnPropertyChanged(nameof(PlayText));
        PlayCommand.NotifyCanExecuteChanged();
        PlayOnCommand.NotifyCanExecuteChanged();
    }

    private bool CanPlayNow => !IsGameRunning && !IsLaunching;

    // раз в пару секунд: запущена ли игра активного клиентского профиля
    private readonly IUiTimer _gameWatch = Ui.Timer(TimeSpan.FromSeconds(2));
    private bool _gameChecking;

    // наблюдение за запущенной игрой: её логи — с момента запуска; вылет или закрытие мира — окно с виновником.
    // Сессия привязана к профилю, где запущена игра, — переключение активного профиля её не обрывает
    private Core.Diagnostics.GameSession? _session;
    private GameProfile? _sessionProfile;
    private readonly List<(string ProfileId, Func<Task> Action)> _afterGameExit = [];

    // ---- «Ошибки модов» активного профиля: «!» у выбора профиля, когда их много

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorBadge), nameof(ErrorBadgeTip))]
    private Core.Diagnostics.ModErrorReport? _errorReport;

    public bool HasErrorBadge => ErrorReport is { IsNoisy: true, Dismissed: false };

    public string ErrorBadgeTip => ErrorReport is { } r ? Loc.T("errs.badgeTip", r.Mods.Count, r.Total) : "";

    private int _errorTicks;

    /// <summary>Отчёт профиля: из прошлых запусков (с диска).</summary>
    private void LoadErrorReport() =>
        ErrorReport = ActiveProfile is { } p ? Core.Diagnostics.ModErrorReport.Load(p.Model.Id) : null;

    /// <summary>Свежий отчёт запуска: сохраняем; «скрыто» держится до конца этого запуска (мод шумит постоянно —
    /// «!» иначе возвращалась бы каждые полминуты), следующий запуск с ошибками покажет её снова.</summary>
    private void UpdateErrorReport(GameProfile profile, Core.Diagnostics.ModErrorReport report)
    {
        var old = Core.Diagnostics.ModErrorReport.Load(profile.Id);
        if (old is { Dismissed: true } && Math.Abs((old.At - report.At).TotalSeconds) < 1) report = report with { Dismissed = true };
        if (report.Total > 0 || old is not null) report.Save(profile.Id);
        if (ActiveProfile?.Model.Id == profile.Id) ErrorReport = report;
    }

    /// <summary>«Скрыть»: игрок посмотрел — «!» уходит до новых ошибок.</summary>
    public void DismissErrorReport(GameProfile profile, Core.Diagnostics.ModErrorReport report)
    {
        var dismissed = report with { Dismissed = true };
        dismissed.Save(profile.Id);
        if (ActiveProfile?.Model.Id == profile.Id) ErrorReport = dismissed;
    }

    [RelayCommand]
    private void ShowErrors()
    {
        if (ActiveProfile is not { } p || ErrorReport is not { } r) return;
        AppWindows.Current.ShowModErrors(this, p.Model, r);
    }

    /// <summary>Игра этого профиля сейчас запущена (по последней проверке).</summary>
    public bool IsGameRunningFor(GameProfile profile) => _sessionProfile?.Id == profile.Id;

    /// <summary>
    /// Сделать, когда игра профиля закроется: игра при выходе перезаписывает свои настройки (и список выключенных
    /// модов) — менять их, пока она открыта, бесполезно.
    /// </summary>
    public void RunAfterGameExit(GameProfile profile, Func<Task> action) => _afterGameExit.Add((profile.Id, action));

    private async Task RunPendingAsync(GameProfile profile)
    {
        var due = _afterGameExit.Where(a => a.ProfileId == profile.Id).ToList();
        _afterGameExit.RemoveAll(a => a.ProfileId == profile.Id);
        foreach (var (_, action) in due)
        {
            try { await action(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { }
        }
        if (due.Count > 0 && ActiveProfile?.Model.Id == profile.Id) Mods.ReloadLocal();
    }

    private async Task CheckGameAsync()
    {
        if (_gameChecking) return;
        _gameChecking = true;
        try
        {
            var active = ActiveProfile is { Kind: ProfileKind.Client } p ? p.Model : null;
            var activePids = active is null ? [] : await Task.Run(() => GameProcess.FindClientPids(active));
            if (ActiveProfile?.Model == active) IsGameRunning = activePids.Count > 0; // пока проверяли, профиль могли сменить

            if (_session is { } session && _sessionProfile is { } watched)
            {
                var alive = watched == active ? activePids.Count > 0 : await Task.Run(() => GameProcess.FindClientPids(watched).Count > 0);
                var finding = await Task.Run(() => alive ? session.Poll(() => Fingerprints(watched)) : session.Finish(() => Fingerprints(watched)));
                // «ошибки модов» — раз в полминуты, пока игра идёт, и после выхода
                if (!alive || ++_errorTicks % 15 == 0)
                    UpdateErrorReport(watched, await Task.Run(() => session.ErrorReport(() => Fingerprints(watched))));
                if (!alive)
                {
                    (_session, _sessionProfile) = (null, null);
                    await RunPendingAsync(watched); // отложенное до выхода из игры (например, выключение мода из окна вылета)
                    ShowUpdatePrompt();
                }
                if (finding is not null) ShowCrash(watched, finding);
            }
            else if (active is not null && activePids.Count > 0)
            {
                // игра этого профиля только что запущена (нами или как угодно) — начинаем следить с её старта
                var started = DateTime.UtcNow;
                try { using var proc = System.Diagnostics.Process.GetProcessById(activePids[0]); started = proc.StartTime.ToUniversalTime(); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                var dataDir = string.IsNullOrWhiteSpace(active.DataDir) ? GameInstall.DefaultDataDir : active.DataDir;
                _session = new Core.Diagnostics.GameSession(dataDir, started, active.GameDir);
                _sessionProfile = active;
                // запуск игры закрывает строку истории модов профиля: всё, что поменяли до него, — одним сеансом
                Core.Mods.ModHistory.MarkLaunch(Core.Mods.ModHistory.FileFor(active.Id), started);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // логи не прочитать — следующая проверка через пару секунд
        }
        finally
        {
            _gameChecking = false;
        }
    }

    /// <summary>Отпечатки модов профиля — по чему узнавать их в логах.</summary>
    private static Core.Diagnostics.ModFingerprints Fingerprints(GameProfile profile) =>
        Core.Diagnostics.ModFingerprints.Build(Core.Mods.ModUpdateService.ScanLocal(ProfileResolver.Resolve(profile)));

    // ---------- «Вышла новая версия eViSTool» ----------

    private bool _updatePromptShown;
    private int _updatePromptWaits;

    /// <summary>
    /// Окно о новой версии — раз за запуск программы. Не поверх игры: пока она запущена или запускается, ждём её выхода.
    /// Версию, которую попросили пропустить, не предлагаем (ссылка в боковой панели остаётся) — кроме рекомендуемой.
    /// </summary>
    private void ShowUpdatePrompt()
    {
        if (_updatePromptShown || About.AvailableUpdate is not { } release || About.IsUpdating
            || (release.Version.ToString() == _settings.SkippedAppVersion && !About.IsRecommended)
            || IsGameRunning || IsLaunching || _sessionProfile is not null)
            return;
        if (!Ui.Platform.IsMainWindowVisible)
        {
            // проверка ответила раньше, чем показалось главное окно, — повторить, когда программа освободится
            if (++_updatePromptWaits <= 20)
                Ui.PostIdle(ShowUpdatePrompt);
            return;
        }
        _updatePromptShown = true;
        // не модально: программой можно пользоваться, пока окно открыто
        AppWindows.Current.ShowUpdatePrompt(About, () =>
        {
            _settings.SkippedAppVersion = release.Version.ToString();
            Save();
        });
    }

    private void ShowCrash(GameProfile profile, Core.Diagnostics.CrashFinding finding)
    {
        // не модально: игра (если мир закрылся, она открыта) и программа остаются доступны
        AppWindows.Current.ShowGameCrash(this, profile, finding);
    }

    /// <summary>Сделать профиль активным и открыть вкладку (0 — «Мои моды», 2 — «Сервер»).</summary>
    public void SwitchTo(GameProfile profile, int tab)
    {
        if (Profiles.FirstOrDefault(p => p.Model.Id == profile.Id) is { } vm && ActiveProfile != vm) ActiveProfile = vm;
        if (ActiveProfile?.Model.Id == profile.Id) SelectedTab = tab;
    }

    // ---- падения серверов: следим за всеми серверными профилями (свои — через их агентов, удалённые — по коду),
    // а не только за активным: сервер упал, пока открыт клиент, — оповещение всё равно приходит
    private readonly IUiTimer _serverWatch = Ui.Timer(TimeSpan.FromSeconds(15));
    private readonly Dictionary<string, string?> _seenServerCrash = [];
    private readonly DateTime _startedAt = DateTime.Now;
    private bool _serverChecking;

    private async Task CheckServersAsync()
    {
        if (_serverChecking) return;
        _serverChecking = true;
        try
        {
            foreach (var profile in Profiles.Where(p => p.Model.Kind == ProfileKind.Server).Select(p => p.Model).ToList())
            {
                Core.Server.ServerCrashInfo? crash;
                Core.Diagnostics.ModErrorReport? errors;
                try
                {
                    using var client = profile.IsRemote
                        ? Core.Server.Remote.RemoteSecret.Unprotect(profile.RemoteCode) is { } code ? Core.Server.AgentClient.ForRemote(code) : null
                        : Core.Server.AgentClient.TryConnect(profile.Id);
                    if (client is null) continue; // агента нет — сервер не работает и не падал под нами
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    var status = await client.StatusAsync(cts.Token);
                    (crash, errors) = (status.LastCrash, status.ModErrors);
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException
                                               or IOException or Newtonsoft.Json.JsonException)
                {
                    continue; // нет связи — спросим в следующий раз
                }

                // «ошибки модов» сервера — в «!» у профиля (и сохраняются, как у игры: «скрыть» помнится)
                if (errors is not null) UpdateErrorReport(profile, errors);

                var first = !_seenServerCrash.ContainsKey(profile.Id);
                var seen = _seenServerCrash.GetValueOrDefault(profile.Id);
                _seenServerCrash[profile.Id] = crash?.Id;
                if (crash is null || crash.Id == seen) continue;
                if (first && crash.At < _startedAt) continue; // упал ещё до запуска окна — не тревожим задним числом
                AppWindows.Current.ShowServerCrash(this, profile, crash);
            }
        }
        finally
        {
            _serverChecking = false;
        }
    }

    public string PlayTip => ActiveProfile is not { Kind: ProfileKind.Client } profile ? ""
        : GameLauncher.DataPathFor(profile.Model) is { } dataPath ? Loc.T("play.tipOwnData", profile.Name, dataPath)
        : Loc.T("play.tip", profile.Name);

    partial void OnIsLaunchingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayText));
        PlayCommand.NotifyCanExecuteChanged();
        PlayOnCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Значок игры для блока «Vintage Story» внизу слева — из установленной игры активного профиля, а если у него
    /// своей нет (удалённый профиль) — из любого другого профиля. null — игры нет нигде: показываются буквы «VS».
    /// </summary>
    public IReadOnlyList<string?> IconSearchDirs =>
        [.. new[] { ActiveProfile?.GameDir }.Concat(Profiles.Select(p => p.GameDir)).Append(Core.Game.GameInstall.FindGameDir())];

    private void NotifyPlay()
    {
        _ = CheckGameAsync(); // сменили профиль — у нового своя игра
        OnPropertyChanged(nameof(IconSearchDirs));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(PlayText));
        OnPropertyChanged(nameof(PlayTip));
    }

    /// <summary>«Играть»: с сервером по умолчанию, если он выбран у профиля, иначе просто игра.</summary>
    [RelayCommand(CanExecute = nameof(CanPlayNow))]
    private Task Play() => LaunchAsync(DefaultPlayTarget);

    private async Task LaunchAsync(PlayTarget? server)
    {
        if (ActiveProfile is not { Kind: ProfileKind.Client } profile) return;

        // вторая копия игры с той же папкой данных перезапишет настройки первой: кнопка в это время неактивна,
        // а здесь — на случай, если игру запустили только что (ярлык), а опрос ещё не заметил
        if (await Task.Run(() => GameProcess.FindClientPids(profile.Model).Count > 0))
        {
            IsGameRunning = true;
            return;
        }

        try
        {
            GameLauncher.Launch(profile.Model, server);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Ui.Warn(Loc.T("play.failed", ex.Message));
            return;
        }

        // процесс уже есть — дальше кнопку держит опрос («Игра запущена»)
        IsLaunching = true;
        try
        {
            await CheckGameAsync();
            if (!IsGameRunning) await Task.Delay(TimeSpan.FromSeconds(3));
        }
        finally { IsLaunching = false; }
        await CheckGameAsync();
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
    private void OpenDataFolder() => Ui.OpenFolder(Core.AppPaths.Root);

    public MainViewModel()
    {
        _settings = _store.Load();
        Loc.Instance.SetLanguage(_settings.Language); // до того, как VM начнут собирать тексты
        foreach (var p in _settings.Profiles) Profiles.Add(new ProfileViewModel(p, OnProfileChanged));

        _allowUnstable = _settings.AllowUnstable;
        _alsoInstallWithoutAsking = _settings.AlsoInstallWithoutAsking;
        _copyWorldsBeforeModChanges = _settings.CopyWorldsBeforeModChanges;
        _autoCheckUpdates = _settings.AutoCheckUpdates;
        _selectedLanguage = Languages.FirstOrDefault(l => l.Value == Loc.Instance.Language) ?? Languages[0];
        _activeProfile = Profiles.FirstOrDefault(p => p.Model == _settings.ActiveProfile);
        _editedProfile = _activeProfile;
        OnEditedProfileChanged(_editedProfile); // поле выше задано мимо свойства — связи и копии миров собрать самим

        var db = new ModDbClient();
        Mods = new ModsViewModel(this, db);
        Catalog = new CatalogViewModel(this, db);
        ModConfig = new ModConfigViewModel(this);
        Server = new ServerViewModel(this);
        About.Profiles = () => _settings.Profiles;
        Server.OnProfileSwitched();
        Save(); // перенос настроек старого формата сразу на диск
        Mods.OnProfileSwitched(); // список модов виден сразу, без сети
        Mods.SetActive(SelectedTab == 0); // и сам обновляется, если моды поменяли в другом окне
        About.UpdateFound += () => ShowUpdatePrompt();
        _ = About.CheckQuietlyAsync(); // новая версия программы — окно «Вышла новая версия» и подсказка в боковой панели

        // Удалённый доступ работает, пока открыто это окно (или пока работает сервер): держим агентов таких профилей
        // запущенными. Закрыли окно и сервер не работает — агент через пару минут выйдет, в системе ничего не остаётся.
        _keepRemote = Ui.Timer(TimeSpan.FromSeconds(30));
        _keepRemote.Tick += (_, _) => _ = KeepRemoteAgentsAsync();
        _keepRemote.Start();
        _gameWatch.Tick += (_, _) => _ = CheckGameAsync();
        _gameWatch.Start();
        _exeWatch.Tick += (_, _) => CheckExeOnDisk();
        _exeWatch.Start();
        LoadErrorReport();
        _serverWatch.Tick += (_, _) => _ = CheckServersAsync();
        _serverWatch.Start();
        _ = CheckGameAsync();
        _ = KeepRemoteAgentsAsync();
    }

    private readonly IUiTimer _keepRemote;

    // ---------- eViSTool обновили на диске, пока окно открыто ----------

    /// <summary>
    /// Раз в 10 секунд — не лежит ли на месте exe этого окна версия новее запущенной. Так бывает после «Обновить на
    /// сервере» с другого компьютера: агент подменил файлы и перезапустился, а открытое здесь окно жило бы старой версией
    /// (видно на Windows-сервере по RDP). Нечего терять — перезапускаемся сами; есть несохранённое или идёт установка —
    /// ссылка «перезапустить» в боковой панели, решает человек.
    /// </summary>
    private readonly IUiTimer _exeWatch = Ui.Timer(TimeSpan.FromSeconds(10));

    /// <summary>Версия на диске, когда сам перезапуск отложен (есть несохранённое); null — перезапускать нечего.</summary>
    [ObservableProperty] private string? _updatedOnDisk;
    public bool HasUpdatedOnDisk => UpdatedOnDisk is not null;
    public string UpdatedOnDiskText => UpdatedOnDisk is { } v ? Loc.T("shell.updatedOnDisk", v) : "";
    partial void OnUpdatedOnDiskChanged(string? value)
    {
        OnPropertyChanged(nameof(HasUpdatedOnDisk));
        OnPropertyChanged(nameof(UpdatedOnDiskText));
    }

    // какой exe был при запуске (время и размер): пока он тот же, версию не читаем
    private (DateTime, long)? _exeStamp;

    private void CheckExeOnDisk()
    {
        // не Environment.ProcessPath: обновление переименовывает работающий exe в .old, и Windows отдаёт уже его
        var exe = MainExe;
        if (About.UpdatedHere) return;
        (DateTime, long) stamp;
        try
        {
            var file = new FileInfo(exe);
            if (!file.Exists) return;
            stamp = (file.LastWriteTimeUtc, file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        _exeStamp ??= stamp;
        if (stamp == _exeStamp) return;

        // версию читаем с копии: для пути загруженного exe Windows отдаёт ресурсы из памяти, то есть старую версию
        var copy = Path.Combine(Path.GetTempPath(), $"evistool-version-{Environment.ProcessId}.exe");
        string? onDisk;
        try
        {
            File.Copy(exe, copy, overwrite: true);
            onDisk = System.Diagnostics.FileVersionInfo.GetVersionInfo(copy).ProductVersion?.Split('+')[0];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // файл как раз дописывают
        finally
        {
            try { File.Delete(copy); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (!Core.Versioning.ModVersion.TryParse(onDisk, out var disk)
            || !Core.Versioning.ModVersion.TryParse(Core.Server.AgentProtocol.AppVersion, out var running)
            || disk.CompareTo(running) <= 0)
        {
            _exeStamp = stamp; // тот же или старее — запоминаем и молчим
            return;
        }

        // терять нечего — перезапуск сразу; иначе ждём человека (и проверяем снова: правки могли сохранить)
        var busy = Mods.IsBusy || Server.Config.IsDirty || ModConfig.IsDirty;
        UpdatedOnDisk = busy ? onDisk : null;
        if (!busy) RestartUpdated();
    }

    private static string MainExe => Path.Combine(AppContext.BaseDirectory, Core.AppUpdate.AppUpdater.MainExe);

    [RelayCommand]
    private void RestartUpdated()
    {
        _exeWatch.Stop();
        Ui.Platform.RestartAfterClose(MainExe);
        if (Ui.Platform.IsMainWindowVisible) _exeWatch.Start(); // закрытие отменили (есть вопрос «сохранить?»)
    }

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
    private void ShowAbout() => SelectedTab = AppTab.About;

    /// <summary>Смену профиля отменили (правки конфига) — выбор в списке надо вернуть на текущий профиль.</summary>
    public event Action? ProfileSwitchDeclined;

    private void OnActiveProfileChanged(ProfileViewModel? value)
    {
        _settings.ActiveProfileId = value?.Model.Id;
        Save();
        NotifyPlay();
        Mods.OnProfileSwitched();
        Server.OnProfileSwitched();
        LoadErrorReport();
        if (SelectedTab == AppTab.ModConfig) _ = ModConfig.LoadAsync();
    }

    partial void OnAllowUnstableChanged(bool value)
    {
        _settings.AllowUnstable = value;
        Save();
        Server?.Schedule.SyncPolicy(); // агент обновляет моды сервера при перезапуске — с тем же правилом
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
        SelectedTab = AppTab.Catalog;
        await Catalog.ShowModAsync(assetId, modId, name);
    }

    /// <summary>Сохранить настройки (например, после закрепления версии мода).</summary>
    /// <summary>Сохранить настройки (из «Моих модов»: закрепления, пропуски версий) — и отдать их агенту сервера:
    /// он соблюдает их, обновляя моды при перезапуске (не поменялись — ничего не шлётся).</summary>
    public void SaveSettings()
    {
        Save();
        Server?.Schedule.SyncPolicy();
    }

    /// <summary>
    /// Перед закрытием программы: несохранённые правки конфигурации сервера, затем конфига мода — каждое
    /// «Сохранить? Да / Нет / Отмена». false — остаться (отменили или сохранить не удалось). Раньше окна подписывались
    /// на закрытие каждое само; теперь окно спрашивает один раз здесь.
    /// </summary>
    public bool ConfirmExit() => Server.Config.ConfirmClose() && ModConfig.ConfirmLeave();

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
        if (AppWindows.Current.ClientProfile(source, clone: false, Loc.T("profile.newClient", Profiles.Count + 1)) is { } created)
            AddProfile(created);
    }

    [RelayCommand]
    private void AddServerProfile()
    {
        if (AskNewProfile(ProfileKind.Server, Loc.T("profile.newServer", Profiles.Count + 1)) is not { DataDir: { } dataDir } dlg) return;

        // сервер на другом компьютере: профиль без папок, код подключения — зашифрованным
        if (dlg.Existing == ExistingProfileMode.Remote && dlg.RemoteCode is { } code)
        {
            AddProfile(new GameProfile { Name = dlg.ProfileName, Kind = ProfileKind.Server, RemoteCode = Core.Server.Remote.RemoteSecret.Protect(code) });
            return;
        }

        // данные сервера уже есть: взять папку как есть или скопировать её в профиль (как клон: мир, конфиги, данные модов и игроков)
        if (dlg.Existing == ExistingProfileMode.UseInPlace && dlg.ExistingDir is { } inPlace)
        {
            AddProfile(new GameProfile { Name = dlg.ProfileName, Kind = ProfileKind.Server, GameDir = KnownGameDir(), DataDir = inPlace });
            return;
        }
        if (dlg.Existing == ExistingProfileMode.Import && dlg.ExistingDir is { } from)
        {
            var source = new GameProfile { Name = Path.GetFileName(from.TrimEnd('\\', '/')), Kind = ProfileKind.Server, GameDir = KnownGameDir(), DataDir = from };
            // сервер из этой папки игры работает — копия мира может получиться битой, окно не даст начать
            var running = GameProcess.FindServerPids(source).Count > 0;
            if (AppWindows.Current.CloneServerProfile(source, running, dlg.ProfileName) is { } imported) AddProfile(imported);
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
    private static NewProfileChoice? AskNewProfile(ProfileKind kind, string suggestedName) =>
        AppWindows.Current.NewProfile(kind, suggestedName);

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
        RefreshLinks();
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
            if (AppWindows.Current.ClientProfile(profile.Model, clone: true, Loc.T("clone.copySuffix", profile.Name)) is { } copy)
                AddProfile(copy);
            return;
        }

        var running = await IsServerRunningAsync(profile.Model);
        if (AppWindows.Current.CloneServerProfile(profile.Model, running) is { } clone) AddProfile(clone);
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
        // удаляем активный — сначала правки его конфига (отмена — профиль не удаляем)
        if (profile == ActiveProfile && !Server.Config.ConfirmSwitch()) return;

        if (model.Kind == ProfileKind.Server && await IsServerRunningAsync(model))
        {
            Ui.Warn(Loc.T("settings.removeRunning", model.Name));
            return;
        }

        // Папку данных предлагаем удалить только там, где её завела сама программа (…\ServerProfiles\имя, …\ClientProfiles\имя)
        // и где она не нужна другому профилю. Чужие папки (VintagestoryData клиента и т. п.) не трогаем никогда.
        var dir = model.DataDir;
        var created = !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)
                      && (model.Kind == ProfileKind.Server ? ServerProfileLayout.IsInContainer(dir) : ClientProfileLayout.IsInContainer(dir));
        // нужна ли папка другим: не только их папки данных внутри, но и моды (общие моды клона) и файл мира
        var users = created ? ProfileUsage.UsersOf(dir!, Profiles.Where(p => p != profile).Select(p => p.Model)) : [];
        var ownFolder = created && users.Count == 0;
        var deleteData = false;
        if (ownFolder)
        {
            var size = await Task.Run(() => FolderSize(dir!));
            // по умолчанию — «Нет»: случайный Enter не должен уносить мир
            var answer = Ui.Ask(Loc.T("settings.removeAskData", model.Name, dir, Sizes.Format(size)), AskButtons.YesNoCancel, byDefault: Answer.No, warning: true);
            if (answer == Answer.Cancel) return;
            deleteData = answer == Answer.Yes;
        }
        else if (!Ui.Confirm(users.Count > 0
                         // папку используют другие профили — удалить её не предлагаем, объясняем почему
                         ? Loc.T("settings.removeAskShared", model.Name, dir, string.Join("\n", users.Select(u => "• " + u.Describe())))
                         : Loc.T("settings.removeAsk", model.Name, string.IsNullOrWhiteSpace(dir) ? "—" : dir), byDefault: Answer.No))
        {
            return;
        }

        if (deleteData)
        {
            try
            {
                Ui.Platform.MoveToTrash(dir!); // в Корзину, не насовсем: мир можно вернуть
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // папку убрать не удалось — профиль оставляем, чтобы можно было повторить
                Ui.Warn(Loc.T("settings.removeDataFailed", dir, ex.Message));
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
        foreach (var p in _settings.Profiles) p.LinkedProfiles.RemoveAll(id => id == model.Id); // связи с удалённым — не нужны
        RefreshLinks();
        Save();
    }
}

/// <summary>Номера вкладок — в порядке боковой панели (MainWindow.xaml).</summary>
public static class AppTab
{
    public const int Mods = 0, Catalog = 1, ModConfig = 2, Server = 3, Settings = 4, About = 5;
}

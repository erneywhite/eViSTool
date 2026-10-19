using eViSTool.Presentation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Hosting;
using eViSTool.Core.ModDb;
using eViSTool.Core.Profiles;
using eViSTool.Core.Settings;
using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Главное окно — фасад для разметки и моделей вкладок. Здесь вкладки, «ошибки модов» активного профиля и связка
/// фоновых наблюдателей; профили — в MainViewModel.Profiles.cs, «Играть» — в MainViewModel.Play.cs, настройки
/// программы — в MainViewModel.Settings.cs. Работа без привязок вынесена в свои классы: наблюдение за игрой
/// (<see cref="GameWatcher"/>), за падениями серверов (<see cref="ServerCrashWatcher"/>), агенты удалённого доступа
/// (<see cref="RemoteAgentKeeper"/>), оповещения серверов у хостера (<see cref="HostedWatch"/>), обновление программы
/// (<see cref="UpdatePrompt"/>, <see cref="DiskUpdateWatcher"/>),
/// окна и файлы при добавлении и удалении профилей (<see cref="ProfileActions"/>), сборка меню «Играть» (<see cref="PlayMenu"/>).
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsStore _store = new();
    private readonly AppSettings _settings;

    private readonly GameWatcher _game;
    private readonly ServerCrashWatcher _serverCrashes;
    private readonly RemoteAgentKeeper _remoteAgents;
    private readonly DiskUpdateWatcher _diskUpdate;
    private readonly UpdatePrompt _updatePrompt;
    private readonly ProfileActions _profileActions;

    public ModsViewModel Mods { get; }
    public CatalogViewModel Catalog { get; }
    public AboutViewModel About { get; } = new();
    public ServerViewModel Server { get; }

    /// <summary>Открытая вкладка — номер из <see cref="AppTab"/> (порядок как в боковой панели).</summary>
    [ObservableProperty] private int _selectedTab;

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
        Mods.SetActive(value == AppTab.Mods);
        Server.SetSectionActive(value == AppTab.Server); // лог сервера у хостера читается, только пока раздел открыт
        if (value == AppTab.Catalog) _ = Catalog.EnsureLoadedAsync(); // каталог грузим только когда он нужен
        if (value == AppTab.ModConfig) _ = ModConfig.LoadAsync();    // конфиги — тоже, и свежие при каждом заходе
    }

    /// <summary>Каналы оповещений этого компьютера — блок в «Настройках».</summary>
    public NotifyChannelsViewModel Notify { get; } = new();

    /// <summary>Проверки обновлений модов серверов у хостера: общие у сторожа оповещений и плашки «Обновить всё».</summary>
    public HostedModUpdates HostedUpdates { get; }

    /// <summary>Оповещения серверов у хостера: агента там нет, шлёт само окно, пока открыто.</summary>
    public HostedWatch HostedWatch { get; }

    /// <summary>Копии мира серверов у хостера: скачивание и восстановление — общие у вкладки «Копии» и расписания сторожа.</summary>
    public HostedWorldJobs HostedWorldJobs { get; } = new();

    // ---- наблюдение за запущенной игрой (сама работа — в GameWatcher)

    /// <summary>Игра этого профиля сейчас запущена (по последней проверке).</summary>
    public bool IsGameRunningFor(GameProfile profile) => _game.IsRunningFor(profile);

    /// <summary>
    /// Сделать, когда игра профиля закроется: игра при выходе перезаписывает свои настройки (и список выключенных
    /// модов) — менять их, пока она открыта, бесполезно.
    /// </summary>
    public void RunAfterGameExit(GameProfile profile, Func<Task> action) => _game.RunAfterExit(profile, action);

    private Task CheckGameAsync() => _game.CheckAsync();

    /// <summary>Проверили игру профиля <paramref name="active"/> — он ещё активный, если его не сменили, пока проверяли.</summary>
    private void OnGameChecked(GameProfile? active, bool running)
    {
        if (ActiveProfile?.Model == active) IsGameRunning = running; // пока проверяли, профиль могли сменить
    }

    /// <summary>Дела, отложенные до выхода из игры профиля, выполнены — моды активного профиля перечитать.</summary>
    private void OnGameExitActionsDone(GameProfile profile)
    {
        if (ActiveProfile?.Model.Id == profile.Id) Mods.ReloadLocal();
    }

    private void ShowCrash(GameProfile profile, Core.Diagnostics.CrashFinding finding)
    {
        // не модально: игра (если мир закрылся, она открыта) и программа остаются доступны
        AppWindows.Current.ShowGameCrash(this, profile, finding);
    }

    // ---- «Ошибки модов» активного профиля: «!» у выбора профиля, когда их много

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorBadge), nameof(ErrorBadgeTip))]
    private Core.Diagnostics.ModErrorReport? _errorReport;

    public bool HasErrorBadge => ErrorReport is { IsNoisy: true, Dismissed: false };

    public string ErrorBadgeTip => ErrorReport is { } r ? Loc.T("errs.badgeTip", r.Mods.Count, r.Total) : "";

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

    // ---------- «Вышла новая версия eViSTool» ----------

    /// <summary>Окно о новой версии — раз за запуск программы и не поверх игры (условия — в <see cref="UpdatePrompt"/>).</summary>
    private void ShowUpdatePrompt() => _updatePrompt.Show();

    /// <summary>Сделать профиль активным и открыть вкладку (номер из <see cref="AppTab"/>).</summary>
    public void SwitchTo(GameProfile profile, int tab)
    {
        if (Profiles.FirstOrDefault(p => p.Model.Id == profile.Id) is { } vm && ActiveProfile != vm) ActiveProfile = vm;
        if (ActiveProfile?.Model.Id == profile.Id) SelectedTab = tab;
    }

    public MainViewModel() : this(new ModDbClient()) { }

    /// <summary>Главная модель с данной модбазой (у «Моих модов» и каталога она общая).</summary>
    /// <param name="db">Тесты дают поддельную, чтобы проверка обновлений шла без сети.</param>
    internal MainViewModel(ModDbClient db)
    {
        // наблюдатели за игрой, падениями серверов и exe на диске — до всего остального, чтобы модели ниже могли спрашивать
        // их уже при сборке; запускаются в конце конструктора, рядом с агентами удалённого доступа
        _game = new GameWatcher(() => ActiveProfile is { Kind: ProfileKind.Client } p ? p.Model : null, OnGameChecked,
            UpdateErrorReport, OnGameExitActionsDone, ShowUpdatePrompt, ShowCrash);
        _serverCrashes = new ServerCrashWatcher(Profiles, UpdateErrorReport,
            (profile, crash) => AppWindows.Current.ShowServerCrash(this, profile, crash));
        _diskUpdate = new DiskUpdateWatcher(() => About.UpdatedHere, OnUpdatedOnDisk);

        _profileActions = new ProfileActions(Profiles, () => ActiveProfile);

        _settings = _store.Load();
        _updatePrompt = new UpdatePrompt(About, () => _settings.SkippedAppVersion, version =>
        {
            _settings.SkippedAppVersion = version;
            Save();
        }, () => IsGameRunning || IsLaunching || _game.IsWatching);
        HostedBackupFolders.Assign(_settings.Profiles, HostedBackupsRoot); // папки копий мира у хостера; на диск уйдёт с Save() ниже
        Loc.Instance.SetLanguage(_settings.Language); // до того, как VM начнут собирать тексты
        foreach (var p in _settings.Profiles) Profiles.Add(new ProfileViewModel(p, OnProfileChanged, _ => Save()));

        _allowUnstable = _settings.AllowUnstable;
        _alsoInstallWithoutAsking = _settings.AlsoInstallWithoutAsking;
        _copyWorldsBeforeModChanges = _settings.CopyWorldsBeforeModChanges;
        _systemWindowFrame = _settings.SystemWindowFrame;
        _gameBackground = _settings.GameBackground;
        _autoCheckUpdates = _settings.AutoCheckUpdates;
        _selectedLanguage = Languages.FirstOrDefault(l => l.Value == Loc.Instance.Language) ?? Languages[0];
        _activeProfile = Profiles.FirstOrDefault(p => p.Model == _settings.ActiveProfile);
        _editedProfile = _activeProfile;
        OnEditedProfileChanged(_editedProfile); // поле выше задано мимо свойства — связи и копии миров собрать самим

        // обновления модов у хостера — тем же путём, что «Проверить» в «Моих модах», с той же настройкой пре-релизов
        HostedUpdates = new HostedModUpdates((target, ct) => Core.Mods.ModUpdatesDue.CheckAsync(target, new Core.Mods.ModUpdateService(db), AllowUnstable, ct));
        HostedWatch = new HostedWatch(Profiles, HostedUpdates, jobs: HostedWorldJobs); // и копии мира по расписанию
        Mods = new ModsViewModel(this, db);
        Catalog = new CatalogViewModel(this, db);
        ModConfig = new ModConfigViewModel(this);
        Server = new ServerViewModel(this);
        About.Profiles = () => _settings.Profiles;
        Server.OnProfileSwitched();
        Save(); // перенос настроек старого формата сразу на диск
        Mods.OnProfileSwitched(); // список модов виден сразу, без сети
        Mods.SetActive(SelectedTab == AppTab.Mods); // и сам обновляется, если моды поменяли в другом окне
        About.UpdateFound += () => ShowUpdatePrompt();
        _ = About.CheckQuietlyAsync(); // новая версия программы — окно «Вышла новая версия» и подсказка в боковой панели

        // Удалённый доступ работает, пока открыто это окно (или пока работает сервер): держим агентов таких профилей
        // запущенными. Закрыли окно и сервер не работает — агент через пару минут выйдет, в системе ничего не остаётся.
        _remoteAgents = new RemoteAgentKeeper(Profiles);
        _remoteAgents.Start();
        _game.Start();
        _diskUpdate.Start();
        LoadErrorReport();
        _serverCrashes.Start();
        HostedWatch.Start(); // у хостера оповещения шлёт окно: лог раз в минуту, обновления модов раз в 6 часов, копии мира по расписанию
        _ = CheckGameAsync();
        _ = _remoteAgents.KeepAsync();
    }

    // ---------- eViSTool обновили на диске, пока окно открыто (проверка — в DiskUpdateWatcher) ----------

    /// <summary>Версия на диске, когда сам перезапуск отложен (есть несохранённое); null — перезапускать нечего.</summary>
    [ObservableProperty] private string? _updatedOnDisk;
    public bool HasUpdatedOnDisk => UpdatedOnDisk is not null;
    public string UpdatedOnDiskText => UpdatedOnDisk is { } v ? Loc.T("shell.updatedOnDisk", v) : "";
    partial void OnUpdatedOnDiskChanged(string? value)
    {
        OnPropertyChanged(nameof(HasUpdatedOnDisk));
        OnPropertyChanged(nameof(UpdatedOnDiskText));
    }

    /// <summary>
    /// На месте exe лежит версия новее запущенной. Нечего терять — перезапускаемся сами; есть несохранённое или идёт
    /// установка — ссылка «перезапустить» в боковой панели, решает человек.
    /// </summary>
    private void OnUpdatedOnDisk(string onDisk)
    {
        // терять нечего — перезапуск сразу; иначе ждём человека (и проверяем снова: правки могли сохранить)
        var busy = Mods.IsBusy || Server.Config.IsDirty || ModConfig.IsDirty;
        UpdatedOnDisk = busy ? onDisk : null;
        if (!busy) RestartUpdated();
    }

    [RelayCommand]
    private void RestartUpdated() => _diskUpdate.Restart();

    [RelayCommand]
    private void ShowAbout() => SelectedTab = AppTab.About;

    /// <summary>Перейти на вкладку «Каталог» и открыть там мод.</summary>
    public async Task ShowInCatalogAsync(long? assetId, string? modId, string? name)
    {
        SelectedTab = AppTab.Catalog;
        await Catalog.ShowModAsync(assetId, modId, name);
    }

    /// <summary>
    /// Перед закрытием программы: несохранённые правки конфигурации сервера, затем конфига мода — каждое
    /// «Сохранить? Да / Нет / Отмена». false — остаться (отменили или сохранить не удалось). Раньше окна подписывались
    /// на закрытие каждое само; теперь окно спрашивает один раз здесь.
    /// </summary>
    public bool ConfirmExit() => Server.Config.ConfirmClose() && ModConfig.ConfirmLeave();
}

/// <summary>
/// Номера вкладок — в порядке боковой панели и страниц (MainWindow.axaml). «Профили» — всё про конкретный профиль,
/// «Настройки» — общее для всей программы.
/// </summary>
public static class AppTab
{
    public const int Mods = 0, Catalog = 1, ModConfig = 2, Server = 3, Profiles = 4, Settings = 5, About = 6;
}

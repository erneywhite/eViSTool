using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Окно «Новый профиль»: название. У серверного профиля сразу видно, какая папка данных будет создана: она получает имя
/// профиля (…\VintagestoryData\ServerProfiles\имя). Ещё у серверного: уже существующие данные сервера (скопировать
/// в профиль или работать прямо в той папке) и сервер на другом компьютере по коду подключения.
/// Выбор сделан — <see cref="Finished"/> с ответом, окно закрывается с ним.
/// </summary>
public sealed partial class NewProfileViewModel : ObservableObject
{
    private readonly string _defaultDataDir;
    private string _folderText = "";
    private bool _canCreate;
    private bool _canUseExisting;
    private string _existingInfo = "";
    private bool _existingInfoIsError;

    public NewProfileViewModel(ProfileKind kind, string suggestedName) : this(kind, suggestedName, GameInstall.DefaultDataDir) { }

    /// <param name="defaultDataDir">
    /// Стандартная папка данных (тесты подставляют свою): в ней создаётся папка серверного профиля, и её же предлагаем
    /// как существующие данные, если сервер с ней уже работал.
    /// </param>
    public NewProfileViewModel(ProfileKind kind, string suggestedName, string defaultDataDir)
    {
        Kind = kind;
        _defaultDataDir = defaultDataDir;
        Title = Loc.T(kind == ProfileKind.Server ? "newprofile.titleServer" : "newprofile.titleClient");
        // сервер уже работал со стандартной папкой (так бывает на машине, где стоит только сервер) — подставляем её
        if (kind == ProfileKind.Server && ProfileResolver.LooksLikeServerData(defaultDataDir)) _existingDir = defaultDataDir;
        _name = suggestedName;
        Refresh();
    }

    public ProfileKind Kind { get; }

    /// <summary>Серверный профиль: видны папка данных, существующие данные и сервер на другом компьютере.</summary>
    public bool IsServer => Kind == ProfileKind.Server;

    /// <summary>Заголовок окна и крупная надпись в нём.</summary>
    public string Title { get; }

    /// <summary>Название профиля (как ввели, с пробелами по краям; в ответ идёт без них).</summary>
    [ObservableProperty] private string _name;

    /// <summary>Папка существующих данных сервера (поле ввода).</summary>
    [ObservableProperty] private string _existingDir = "";

    partial void OnNameChanged(string value) => Refresh();
    partial void OnExistingDirChanged(string value) => Refresh();

    /// <summary>Папка данных, которая будет создана (серверный профиль с непустым названием; иначе пусто).</summary>
    public string FolderText
    {
        get => _folderText;
        private set => SetProperty(ref _folderText, value);
    }

    /// <summary>Название не пустое — «Создать» доступна.</summary>
    public bool CanCreate
    {
        get => _canCreate;
        private set
        {
            if (SetProperty(ref _canCreate, value)) OkCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>В папке существующих данных есть конфиги сервера и название не пустое — «Скопировать» и «Использовать как есть» доступны.</summary>
    public bool CanUseExisting
    {
        get => _canUseExisting;
        private set
        {
            if (!SetProperty(ref _canUseExisting, value)) return;
            ImportCommand.NotifyCanExecuteChanged();
            UseInPlaceCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Что нашлось в папке существующих данных: конфиги (с миром) или «не похоже на данные сервера»; папка не указана — пусто.</summary>
    public string ExistingInfo
    {
        get => _existingInfo;
        private set => SetProperty(ref _existingInfo, value);
    }

    /// <summary>Папка указана, но конфигов сервера в ней нет — <see cref="ExistingInfo"/> красным.</summary>
    public bool ExistingInfoIsError
    {
        get => _existingInfoIsError;
        private set => SetProperty(ref _existingInfoIsError, value);
    }

    /// <summary>Ответ окна; null — ещё не выбрали.</summary>
    public NewProfileChoice? Result { get; private set; }

    /// <summary>Выбор сделан — окно закрывается с этим ответом.</summary>
    public event Action<NewProfileChoice>? Finished;

    /// <summary>«Подключить по коду» с пустым названием — вернуть фокус в поле названия.</summary>
    public event Action? FocusNameRequested;

    private static string Clean(string? text) => text?.Trim() ?? "";

    /// <summary>
    /// Пересчёт после правки названия или папки. Как в окне на WPF, папка перечитывается и при смене названия: от него
    /// зависят кнопки существующих данных.
    /// </summary>
    private void Refresh()
    {
        var name = Clean(Name);
        CanCreate = name.Length > 0;

        var dir = Clean(ExistingDir);
        var configs = dir.Length > 0 && Directory.Exists(dir) ? ProfileCloner.FindConfigs(dir) : [];
        var found = configs.Count > 0;
        CanUseExisting = found && name.Length > 0;
        ExistingInfo = dir.Length == 0 ? ""
            : found ? Loc.T("newprofile.existingFound", string.Join(", ", configs.Select(c => c.WorldName is { Length: > 0 } w ? $"{c.FileName} ({w})" : c.FileName)))
            : Loc.T("newprofile.existingNone");
        ExistingInfoIsError = dir.Length > 0 && !found;

        if (IsServer) FolderText = name.Length > 0 ? ServerProfileLayout.SuggestDir(_defaultDataDir, name) : "";
    }

    /// <summary>«Создать»: серверному — новая папка данных по названию, клиентскому — без папки.</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Ok()
    {
        var name = Clean(Name);
        if (name.Length == 0) return;
        Finish(new NewProfileChoice(name, IsServer ? ServerProfileLayout.SuggestDir(_defaultDataDir, name) : null, ExistingProfileMode.None, null, null));
    }

    /// <summary>«Скопировать в новый профиль»: данные из папки копируются, исходная остаётся как есть.</summary>
    [RelayCommand(CanExecute = nameof(CanUseExisting))]
    private void Import() => FinishExisting(ExistingProfileMode.Import);

    /// <summary>«Использовать как есть»: профиль работает прямо в этой папке.</summary>
    [RelayCommand(CanExecute = nameof(CanUseExisting))]
    private void UseInPlace() => FinishExisting(ExistingProfileMode.UseInPlace);

    private void FinishExisting(ExistingProfileMode mode)
    {
        var name = Clean(Name);
        if (name.Length == 0) return;
        var dir = Clean(ExistingDir);
        Finish(new NewProfileChoice(name, dir, mode, null, dir));
    }

    /// <summary>«Подключить по коду»: сервер на другом компьютере, папки данных у профиля нет (DataDir — пустая строка).</summary>
    [RelayCommand]
    private void Connect()
    {
        var name = Clean(Name);
        if (name.Length == 0)
        {
            FocusNameRequested?.Invoke();
            return;
        }
        if (AppWindows.Current.AskConnectionCode(Loc.T("newprofile.connectOk")) is not { } code) return;
        Finish(new NewProfileChoice(name, "", ExistingProfileMode.Remote, code, null));
    }

    /// <summary>«Обзор…» у папки существующих данных.</summary>
    [RelayCommand]
    private void Browse()
    {
        if (Ui.Platform.PickFolder(Loc.T("newprofile.existingPick"), Clean(ExistingDir)) is { } dir) ExistingDir = dir;
    }

    private void Finish(NewProfileChoice choice)
    {
        Result = choice;
        Finished?.Invoke(choice);
    }
}

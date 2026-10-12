using eViSTool.Core.Diagnostics;
using eViSTool.Core.Game;
using eViSTool.Core.Notifications;
using eViSTool.Core.Packs;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Что делать с уже существующими данными сервера в окне «Новый профиль».</summary>
public enum ExistingProfileMode { None, Import, UseInPlace, Remote }

/// <summary>Ответ окна «Новый профиль»: название и откуда брать данные.</summary>
/// <param name="DataDir">Папка данных нового серверного профиля (для клиентского — null).</param>
/// <param name="RemoteCode">Для Remote: код подключения к серверу на другом компьютере.</param>
/// <param name="ExistingDir">Папка существующих данных сервера (для Import и UseInPlace).</param>
public sealed record NewProfileChoice(string ProfileName, string? DataDir, ExistingProfileMode Existing, ConnectionCode? RemoteCode, string? ExistingDir);

/// <summary>Ответ окна бана: срок в единицах команды /ban (minute, hour, day, week, year) и причина.</summary>
public sealed record BanChoice(int Amount, string Unit, string Reason);

/// <summary>Ответ окна дублирования роли: код и название копии (пусто — оставить как у исходной).</summary>
public sealed record RoleCopyChoice(string Code, string Name);

/// <summary>Ответ окна предпросмотра импорта модпака.</summary>
public sealed record PackImportChoice(bool Mirror, bool ApplyConfig);

/// <summary>Ответ окна «Установить также в»: Tag отмеченных профилей и «запомнить как связь».</summary>
public sealed record AlsoInstallAnswer(IReadOnlyList<object> Chosen, bool Remember);

/// <summary>Ответ окна «Сервер в избранном»: название, адрес (с портом или без) и пароль (null — без пароля).</summary>
public sealed record FavoriteServerChoice(string Name, string Address, string? Password);

/// <summary>
/// Окна программы, которые открывают модели. Модальные возвращают ответ (null — закрыли без выбора), остальные просто
/// показываются. Реализация — у окна (<c>AvaloniaAppWindows</c>); модели знают только этот интерфейс.
/// </summary>
public interface IAppWindows
{
    // ---- профили
    /// <summary>Новый клиентский профиль на основе существующего или его клон; готовый профиль (папка уже создана).</summary>
    GameProfile? ClientProfile(GameProfile source, bool clone, string suggestedName);
    /// <summary>Клон серверного профиля (importName не null — импорт существующих данных); готовый профиль (папка уже скопирована).</summary>
    GameProfile? CloneServerProfile(GameProfile source, bool serverRunning, string? importName = null);
    NewProfileChoice? NewProfile(ProfileKind kind, string suggestedName);
    /// <summary>Вставить и проверить код подключения к серверу на другом компьютере.</summary>
    ConnectionCode? AskConnectionCode(string okText);
    void ShowCleanup(GameProfile profile);
    void ShowFindServer(FindServerViewModel model);
    /// <summary>
    /// Пароль сервера — перед входом или добавлением в избранное. null — отказались; пустая строка — «Ок» с пустым полем.
    /// Владелец — то окно, из которого спросили (окно «Найти сервер»).
    /// </summary>
    string? AskServerPassword(string server);
    /// <summary>Сервер в избранном игры: новый (existing = null) или правка записи. null — отказались. Владелец — как у <see cref="AskServerPassword"/>.</summary>
    FavoriteServerChoice? EditFavoriteServer(PlayTarget? existing);

    // ---- моды
    VersionOption? PickVersion(string heading, IReadOnlyList<VersionOption> options);
    PackExportOptions? ExportPack(string defaultName, int enabledCount, int disabledCount, bool hasModConfig);
    PackImportChoice? ConfirmPackImport(PackImportPlan plan);
    /// <summary>Вставить код набора модов («EV1-…»); null — закрыли. Ответ — разобранный код (моды, выпуски, версия игры).</summary>
    ModCodeSet? AskModCode();
    /// <summary>Показать готовый код набора: блоком, который копируется щелчком (в буфер он уже положен).</summary>
    void ShowModCode(ModCodeShare share);
    /// <summary>«Установить также в»: сам вопрос; правила «без вопроса» и «запомнить» — в <see cref="AlsoInstallPicker"/>.</summary>
    AlsoInstallAnswer? AskAlsoInstall(string hint, string currentName, string currentKind, string currentDetail, IReadOnlyList<AlsoChoice> others);
    void ShowScreenshots(IReadOnlyList<string> urls, int index, string title);

    // ---- сервер
    BanChoice? Ban(string player);
    RoleCopyChoice? DuplicateRole(string sourceCode, string sourceName, IReadOnlyList<string> takenCodes);
    NotifyChannel? EditNotifyChannel(NotifyChannel channel, bool isNew);
    /// <summary>Папка на машине с сервером (список папок берётся у его агента).</summary>
    string? PickRemoteFolder(IServerData data, string? start, string heading);
    /// <summary>Все команды сервера с поиском; выбранная — для поля ввода консоли.</summary>
    ServerCommand? PickServerCommand(ServerViewModel server);

    // ---- немодальные
    void ShowModErrors(MainViewModel main, GameProfile profile, ModErrorReport report);
    /// <summary>Окно «Вышла новая версия». skip — запомнить, что эту версию предлагать не надо.</summary>
    void ShowUpdatePrompt(AboutViewModel about, Action skip);
    void ShowGameCrash(MainViewModel main, GameProfile profile, CrashFinding finding);
    void ShowServerCrash(MainViewModel main, GameProfile profile, ServerCrashInfo crash);

    // ---- главное окно
    /// <summary>Вынести главное окно вперёд: из немодального окна (вылет, ошибки модов) перешли к чему-то в главном.</summary>
    void ActivateMainWindow();
}

/// <summary>Доступ моделей к окнам программы. Окно ставит свою реализацию при запуске, тест — свою.</summary>
public static class AppWindows
{
    private static IAppWindows? _current;

    public static bool IsSet => _current is not null;

    public static IAppWindows Current
    {
        get => _current ?? throw new InvalidOperationException("AppWindows.Current не задан: окно ставит его при запуске, тест — перед проверкой.");
        set => _current = value;
    }
}

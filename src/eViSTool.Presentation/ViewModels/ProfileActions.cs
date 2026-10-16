using System.IO;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Добавить, клонировать и удалить профиль: окна-вопросы и файлы на диске. Список профилей, активный и редактируемый
/// профиль и сохранение настроек остаются у главной модели — отсюда уходят готовые решения: какой профиль добавить
/// (null — отменили) и можно ли убирать профиль из списка.
/// </summary>
/// <param name="profiles">Профили окна (живой список).</param>
/// <param name="active">Активный профиль — читается в момент надобности: после окна-вопроса он может быть уже другим.</param>
public sealed class ProfileActions(IReadOnlyList<ProfileViewModel> profiles, Func<ProfileViewModel?> active)
{
    /// <summary>
    /// Новый клиентский профиль — со своей папкой данных (…\ClientProfiles\имя) на основе существующего клиентского:
    /// текущего, если он клиентский, иначе первого; нет ни одного — на основе стандартной папки игры. null — отменили.
    /// </summary>
    public GameProfile? NewClient()
    {
        var source = (active() is { Kind: ProfileKind.Client } ? active() : profiles.FirstOrDefault(p => p.Kind == ProfileKind.Client))?.Model
                     ?? ProfileResolver.DefaultClient(KnownGameDir());
        return AppWindows.Current.ClientProfile(source, clone: false, Loc.T("profile.newClient", profiles.Count + 1));
    }

    /// <summary>
    /// Новый серверный профиль из окна «Новый профиль»: удалённый (по коду), на существующей папке данных (как есть
    /// или копией) или со своей новой папкой. null — отменили.
    /// </summary>
    public GameProfile? NewServer()
    {
        if (AskNewProfile(ProfileKind.Server, Loc.T("profile.newServer", profiles.Count + 1)) is not { DataDir: { } dataDir } dlg) return null;

        // сервер на другом компьютере: профиль без папок, код подключения — зашифрованным
        if (dlg.Existing == ExistingProfileMode.Remote && dlg.RemoteCode is { } code)
            return new GameProfile { Name = dlg.ProfileName, Kind = ProfileKind.Server, RemoteCode = RemoteSecret.Protect(code) };

        // данные сервера уже есть: взять папку как есть или скопировать её в профиль (как клон: мир, конфиги, данные модов и игроков)
        if (dlg.Existing == ExistingProfileMode.UseInPlace && dlg.ExistingDir is { } inPlace)
            return new GameProfile { Name = dlg.ProfileName, Kind = ProfileKind.Server, GameDir = KnownGameDir(), DataDir = inPlace };
        if (dlg.Existing == ExistingProfileMode.Import && dlg.ExistingDir is { } from)
        {
            var source = new GameProfile { Name = Path.GetFileName(from.TrimEnd('\\', '/')), Kind = ProfileKind.Server, GameDir = KnownGameDir(), DataDir = from };
            // сервер из этой папки игры работает — копия мира может получиться битой, окно не даст начать
            var running = GameProcess.FindServerPids(source).Count > 0;
            return AppWindows.Current.CloneServerProfile(source, running, dlg.ProfileName);
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

        return new GameProfile { Name = dlg.ProfileName, Kind = ProfileKind.Server, GameDir = KnownGameDir(), DataDir = dataDir };
    }

    /// <summary>
    /// Клон профиля со своей папкой данных: серверного — «тот же мир» или «новый мир»,
    /// клиентского — с общими или своими модами, с настройками и мирами по выбору. null — отменили или клонировать нельзя.
    /// </summary>
    public async Task<GameProfile?> CloneAsync(ProfileViewModel? profile)
    {
        if (profile is null || profile.Model.IsRemote) return null; // удалённый сервер клонируется на своём компьютере
        if (profile.Kind == ProfileKind.Client)
            return AppWindows.Current.ClientProfile(profile.Model, clone: true, Loc.T("clone.copySuffix", profile.Name));

        var running = await IsServerRunningAsync(profile.Model);
        return AppWindows.Current.CloneServerProfile(profile.Model, running);
    }

    /// <summary>
    /// Перед удалением профиля из списка: работающий сервер не удаляем; папку данных, заведённую самой программой и не
    /// нужную другим, — по вопросу в Корзину; файлы агента и расписания профиля — удаляем. true — профиль можно убирать
    /// из списка, false — отменили или не вышло (профиль остаётся, чтобы можно было повторить).
    /// </summary>
    public async Task<bool> PrepareRemoveAsync(ProfileViewModel profile)
    {
        var model = profile.Model;
        if (model.Kind == ProfileKind.Server && await IsServerRunningAsync(model))
        {
            Ui.Warn(Loc.T("settings.removeRunning", model.Name));
            return false;
        }

        // Папку данных предлагаем удалить только там, где её завела сама программа (…\ServerProfiles\имя, …\ClientProfiles\имя)
        // и где она не нужна другому профилю. Чужие папки (VintagestoryData клиента и т. п.) не трогаем никогда.
        var dir = model.DataDir;
        var created = !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)
                      && (model.Kind == ProfileKind.Server ? ServerProfileLayout.IsInContainer(dir) : ClientProfileLayout.IsInContainer(dir));
        // нужна ли папка другим: не только их папки данных внутри, но и моды (общие моды клона) и файл мира
        var users = created ? ProfileUsage.UsersOf(dir!, profiles.Where(p => p != profile).Select(p => p.Model)) : [];
        var ownFolder = created && users.Count == 0;
        var deleteData = false;
        if (ownFolder)
        {
            var size = await Task.Run(() => FolderSize(dir!));
            // по умолчанию — «Нет»: случайный Enter не должен уносить мир
            var answer = Ui.Ask(Loc.T("settings.removeAskData", model.Name, dir, Sizes.Format(size)), AskButtons.YesNoCancel, byDefault: Answer.No, warning: true);
            if (answer == Answer.Cancel) return false;
            deleteData = answer == Answer.Yes;
        }
        else if (!Ui.Confirm(users.Count > 0
                         // папку используют другие профили — удалить её не предлагаем, объясняем почему
                         ? Loc.T("settings.removeAskShared", model.Name, dir, string.Join("\n", users.Select(u => "• " + u.Describe())))
                         : Loc.T("settings.removeAsk", model.Name, string.IsNullOrWhiteSpace(dir) ? "—" : dir), byDefault: Answer.No))
        {
            return false;
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
                return false;
            }
        }

        // ключ, адрес агента и расписание этого профиля больше не нужны
        foreach (var file in new[] { AgentProtocol.StateFile(model.Id), AgentProtocol.KeyFile(model.Id),
                     ServerAutomation.FileFor(model.Id) })
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return true;
    }

    /// <summary>Спросить название нового профиля (у серверного от него зависит имя папки данных). null — отменили.</summary>
    private static NewProfileChoice? AskNewProfile(ProfileKind kind, string suggestedName) =>
        AppWindows.Current.NewProfile(kind, suggestedName);

    /// <summary>Папка игры для нового профиля: как у текущего; нет — как у любого другого; нет и там — ищем установку.</summary>
    private string? KnownGameDir() =>
        new[] { active()?.GameDir }.Concat(profiles.Select(p => p.GameDir)).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d))
        ?? GameInstall.FindGameDir();

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
        using var client = AgentClient.TryConnect(profile.Id);
        if (client is null) return false;
        try
        {
            return (await client.StatusAsync()).State != ServerState.Stopped;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return false; // агент не отвечает — сервера под ним нет
        }
    }
}

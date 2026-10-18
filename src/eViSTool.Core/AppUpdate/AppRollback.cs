using eViSTool.Core.Localization;
using eViSTool.Core.Versioning;
using Newtonsoft.Json;

namespace eViSTool.Core.AppUpdate;

/// <summary>Программа eViSTool, чей запуск засчитывается новой версии.</summary>
public enum AppProgram
{
    Window,
    Agent,
}

/// <summary>Файл, который заменило обновление: имя в папке программы и прежний вариант в «*.old» (null — файл пришёл с обновлением).</summary>
internal sealed record ReplacedFile(string Name, string? Old);

/// <summary>Содержимое <see cref="AppRollback.FileName"/>.</summary>
internal sealed class AppRollbackState
{
    /// <summary>Версия файлов в папке программы: её поставило последнее обновление (или откат).</summary>
    public string? Version { get; set; }

    /// <summary>Версия, чьи файлы лежат в «*.old»; null — прежней версии рядом нет.</summary>
    public string? Previous { get; set; }

    /// <summary>Что заменило обновление: при откате эти файлы меняются местами.</summary>
    public List<ReplacedFile> Files { get; set; } = [];

    /// <summary>Первый успешный запуск новой версии; null — ещё не запускалась.</summary>
    public DateTimeOffset? FirstStart { get; set; }

    public int WindowStarts { get; set; }
    public int AgentStarts { get; set; }
}

/// <summary>
/// Прежняя версия программы после самообновления. <see cref="AppUpdater.Install"/> переименовывает прежние файлы в «*.old»;
/// они остаются рядом, пока новая версия не проработала: запустилась успешно <see cref="StartsNeeded"/> раза и с первого
/// запуска прошло не меньше <see cref="KeepAtLeast"/>. До тех пор на прежнюю версию можно вернуться (<see cref="RollBack"/>).
/// Хранится одно поколение: следующее обновление убирает «*.old» предыдущего.
/// <para>
/// Что лежит в «*.old», какой это версии и как запускалась новая — в <see cref="FileName"/> в папке программы. Не в папке
/// данных: «*.old» лежат рядом с программой, у каждой портабельной копии они свои, а папка данных бывает общей
/// (%LOCALAPPDATA%). Записи нет (обновление ставила версия до 0.14.1) или в папку нельзя писать — как раньше: «*.old»
/// убираются при первом же запуске.
/// </para>
/// <para>
/// Запуски окна и агента считаются отдельно: окно, запустившись, поднимает агентов, и один его запуск не должен сойти за
/// два. Если окно новой версии запускали, считаются его запуски, иначе (сервер без окна) — запуски агента.
/// </para>
/// </summary>
public static class AppRollback
{
    public const string FileName = ".previous-version.json";

    /// <summary>Сколько успешных запусков новой версии нужно, чтобы убрать прежнюю.</summary>
    public const int StartsNeeded = 2;

    /// <summary>Сколько прежняя версия хранится с первого запуска новой, даже если запусков уже хватает.</summary>
    public static TimeSpan KeepAtLeast { get; } = TimeSpan.FromDays(1);

    private static StringComparer FileNames { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Версия, на которую можно вернуться из запущенной <paramref name="version"/>; null — некуда: прежнюю уже убрали,
    /// обновление ставила старая версия, файлы в папке поменяли мимо обновления или в папку нельзя писать.
    /// </summary>
    public static string? Available(string appDir, string version) =>
        Read(appDir) is { } state && Tracks(appDir, state, version) && CanWrite(appDir) ? state.Previous : null;

    /// <summary>
    /// Программа <paramref name="program"/> версии <paramref name="version"/> запустилась успешно. Засчитать запуск, а когда
    /// новая версия проработала (<see cref="StartsNeeded"/> запуска и <see cref="KeepAtLeast"/>), убрать «*.old». Прежнюю
    /// версию не храним — убрать их сразу, как раньше. Ошибок не бросает: запуску программы это мешать не должно.
    /// </summary>
    /// <param name="time">Часы (тесты подменяют); null — системные.</param>
    public static void NoteStart(string appDir, AppProgram program, string version, TimeProvider? time = null)
    {
        try
        {
            var path = Path.Combine(appDir, FileName);
            var state = Read(appDir);
            if (state is null || !Tracks(appDir, state, version))
            {
                // прежней версии не храним — хвосты прошлого обновления прочь, как раньше
                AppUpdater.CleanupOld(appDir);
                if (state is null) return;
                // запись не про эти файлы (их поменяли мимо обновления) — она больше не нужна; про эти, но прежняя версия
                // неполная (файл «*.old» удалили руками) — остаётся только версия в папке
                if (!SameVersion(state.Version, version)) DeleteQuietly(path);
                else if (state.Previous is not null) TryWrite(appDir, new AppRollbackState { Version = state.Version });
                return;
            }

            var now = (time ?? TimeProvider.System).GetUtcNow();
            state.FirstStart ??= now;
            if (program == AppProgram.Window) state.WindowStarts++;
            else state.AgentStarts++;
            var proven = Proven(state, now);
            if (proven) state = new AppRollbackState { Version = state.Version };

            // shortcut: окно и агент, стартовавшие в один миг, могут затереть запуск друг друга — прежняя версия тогда
            // хранится на запуск дольше; переделать на замок, если счёт запусков станет важнее суток
            if (!TryWrite(appDir, state))
            {
                // в папку не пишется: запуски не сосчитать и прежнюю версию не вернуть — как раньше
                AppUpdater.CleanupOld(appDir);
                DeleteQuietly(path);
                return;
            }
            if (proven) AppUpdater.CleanupOld(appDir);
            else DeleteUntracked(appDir, state); // хвосты, которые к прежней версии не относятся: от поколений старше, от отката
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // папку не прочитать — значит, и убирать в ней нечего
        }
    }

    /// <summary>
    /// Вернуть прежнюю версию на место запущенной <paramref name="version"/>: тем же путём, что обновление, — текущие файлы
    /// становятся «*.old», прежние встают на их место. Не вышло посередине — всё возвращается как было, в папке остаётся
    /// текущая версия целиком. Возвращает версию, которая теперь стоит. Перезапуск — за вызывающим.
    /// </summary>
    /// <exception cref="InvalidOperationException">Прежней версии рядом нет.</exception>
    /// <exception cref="IOException">Файл не переставить (занят, нет места).</exception>
    /// <exception cref="UnauthorizedAccessException">В папку программы нельзя писать.</exception>
    public static string RollBack(string appDir, string version)
    {
        if (Read(appDir) is not { } state || !Tracks(appDir, state, version))
            throw new InvalidOperationException(Loc.T("update.noPrevious"));

        var moves = new List<(string From, string To)>();
        void Move(string from, string to)
        {
            File.Move(from, to);
            moves.Add((from, to));
        }
        try
        {
            foreach (var file in state.Files)
            {
                var target = Path.Combine(appDir, file.Name);
                // работающий exe удалить нельзя, а переименовать можно — как при обновлении
                if (File.Exists(target)) Move(target, AppUpdater.OldName(target));
                if (file.Old is { } old) Move(Path.Combine(appDir, old), target);
            }
        }
        catch
        {
            foreach (var (from, to) in Enumerable.Reverse(moves))
            {
                try { File.Move(to, from); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }

        // в папке теперь прежняя версия; снятые файлы новой — обычные хвосты «*.old», их уберёт следующий запуск.
        // Вернуться обратно на новую — обновлением, а не этим путём: поколение одно
        if (!TryWrite(appDir, new AppRollbackState { Version = state.Previous })) DeleteQuietly(Path.Combine(appDir, FileName));
        return state.Previous!;
    }

    /// <summary>Какая версия стоит в папке по записи последнего обновления; null — записи нет.</summary>
    internal static string? InstalledVersion(string appDir) => Read(appDir)?.Version;

    /// <summary>
    /// После удачной установки (<see cref="AppUpdater.Install"/>): «*.old» прошлых поколений — прочь (одно поколение),
    /// и записать, что лежит в новых. Версии неизвестны — прежнюю не храним, её «*.old» уберёт следующий запуск.
    /// </summary>
    /// <param name="stale">«*.old», которые лежали до установки.</param>
    internal static void Installed(string appDir, string? previous, string? current, IEnumerable<ReplacedFile> files, IEnumerable<string> stale)
    {
        foreach (var file in stale) DeleteQuietly(file); // занятые (работает агент прошлых версий) уберёт следующий запуск
        AppRollbackState? state = current is null ? null
            : previous is null || SameVersion(previous, current) ? new AppRollbackState { Version = current }
            : new AppRollbackState { Version = current, Previous = previous, Files = [.. files] };
        if (state is null || !TryWrite(appDir, state)) DeleteQuietly(Path.Combine(appDir, FileName));
    }

    /// <summary>Новая версия проработала: нужное число запусков (окна, а если его не запускали, — агента) и сутки с первого.</summary>
    private static bool Proven(AppRollbackState state, DateTimeOffset now)
    {
        var starts = state.WindowStarts > 0 ? state.WindowStarts : state.AgentStarts;
        return starts >= StartsNeeded && state.FirstStart is { } first && now - first >= KeepAtLeast;
    }

    /// <summary>Запись описывает прежнюю версию запущенной <paramref name="version"/>, и все её файлы на месте.</summary>
    private static bool Tracks(string appDir, AppRollbackState state, string version) =>
        state.Previous is { Length: > 0 }
        && SameVersion(state.Version, version)
        && state.Files is { Count: > 0 } files
        && files.All(f => f is not null && PlainName(f.Name)
                          && (f.Old is null || (PlainName(f.Old) && f.Old.EndsWith(".old", StringComparison.OrdinalIgnoreCase)
                                                && File.Exists(Path.Combine(appDir, f.Old)))))
        && files.Any(f => f.Old is not null);

    /// <summary>Просто имя файла в папке программы: запись не должна переставлять файлы где-то ещё.</summary>
    private static bool PlainName(string? name) => name is { Length: > 0 } and not ("." or "..") && Path.GetFileName(name) == name;

    private static bool SameVersion(string? a, string? b) =>
        a is not null && b is not null
        && (ModVersion.TryParse(a, out var x) && ModVersion.TryParse(b, out var y) ? x.Equals(y) : a == b);

    /// <summary>Убрать «*.old», которых нет в записи о прежней версии.</summary>
    private static void DeleteUntracked(string appDir, AppRollbackState state)
    {
        var kept = state.Files.Where(f => f.Old is not null).Select(f => f.Old!).ToHashSet(FileNames);
        foreach (var file in Directory.EnumerateFiles(appDir, "*.old"))
            if (!kept.Contains(Path.GetFileName(file))) DeleteQuietly(file);
    }

    private static AppRollbackState? Read(string appDir)
    {
        try
        {
            var path = Path.Combine(appDir, FileName);
            return File.Exists(path) ? JsonConvert.DeserializeObject<AppRollbackState>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null; // испорчена — как будто её нет
        }
    }

    /// <summary>Записать целиком или никак: через временный файл, который встаёт на место записи.</summary>
    private static bool TryWrite(string appDir, AppRollbackState state)
    {
        var path = Path.Combine(appDir, FileName);
        var tmp = $"{path}.{Guid.NewGuid().ToString("N")[..8]}.tmp";
        try
        {
            File.WriteAllText(tmp, JsonConvert.SerializeObject(state, Formatting.Indented));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(tmp);
            return false;
        }
    }

    /// <summary>Можно ли создавать файлы в папке программы: без этого не переставить ни обновление, ни откат.</summary>
    private static bool CanWrite(string appDir)
    {
        var probe = Path.Combine(appDir, $".write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

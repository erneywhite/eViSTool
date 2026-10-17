using eViSTool.Core.Mods;
using eViSTool.Core.Platform;
using Newtonsoft.Json;

namespace eViSTool.Core.Hosting;

/// <summary>Заменяемый файл установки: где лежал на сервере, его копия на этой машине и размер.</summary>
public sealed record HostedPendingOld(string Path, string Local, long Size);

/// <summary>
/// Начатая установка на сервер у хостера: архив уже залит как <paramref name="Tmp"/> (размер <paramref name="Size"/>),
/// дальше — удалить <paramref name="Removed"/> и переименовать временный файл в <paramref name="Target"/>.
/// Мод и версии — для истории, если установку доведёт следующий скан.
/// </summary>
public sealed record HostedPending(string Tmp, string Target, long Size, IReadOnlyList<HostedPendingOld> Removed,
    string ModId, string? Name, string? From, string? To);

/// <summary>
/// Журнал установки на сервер у хостера: <c>&lt;данные eViSTool&gt;/hosted/&lt;id&gt;/pending.json</c> и копии
/// заменяемых версий рядом, в <c>old/</c>. Лежит не в cache — «Уборка диска» его не сотрёт. Пока журнал есть, установка
/// не закончена: связь оборвалась между «удалить старое» и «переименовать новое». Следующий скан доводит её
/// (<see cref="FinishAsync"/>) или возвращает как было.
/// </summary>
public static class HostedJournal
{
    public const string FileName = "pending.json";

    /// <summary>Папка журнала профиля. Id с путём — исключение: он идёт в путь.</summary>
    public static string DirFor(string profileId) =>
        PathRules.FileIn(Path.Combine(AppPaths.Root, "hosted"), profileId)
        ?? throw new ArgumentException("bad profile id", nameof(profileId));

    /// <summary>Куда класть копии заменяемых версий одной установки.</summary>
    public static string OldDirFor(string profileId) => Path.Combine(DirFor(profileId), "old", Guid.NewGuid().ToString("N")[..12]);

    public static HostedPending? Read(string profileId)
    {
        var file = Path.Combine(DirFor(profileId), FileName);
        try
        {
            return File.Exists(file) ? JsonConvert.DeserializeObject<HostedPending>(File.ReadAllText(file)) : null;
        }
        catch (JsonException)
        {
            return null; // битый журнал ничего не доведёт; копии остаются в old/
        }
    }

    public static void Write(string profileId, HostedPending pending)
    {
        var file = Path.Combine(DirFor(profileId), FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(pending, Formatting.Indented));
        SafeFile.Replace(tmp, file);
    }

    /// <summary>Убрать журнал и копии этой установки (они уже в хранилище версий или больше не нужны).</summary>
    public static void Delete(string profileId, HostedPending pending)
    {
        File.Delete(Path.Combine(DirFor(profileId), FileName));
        foreach (var dir in pending.Removed.Select(r => Path.GetDirectoryName(r.Local)).OfType<string>().Distinct())
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // копии остались в папке журнала — место, но не вред
            }
        }
    }

    /// <summary>
    /// Довести прерванную установку по состоянию сервера. true — новая версия на месте (копии старых переносятся в
    /// хранилище версий, изменение — в историю), false — вернули как было. Сбой связи — исключение, журнал остаётся.
    /// <paramref name="root"/> — корень сервера из профиля (выше него листинги могут быть закрыты).
    /// </summary>
    public static async Task<bool> FinishAsync(IRemoteFiles files, string profileId, HostedPending pending, CancellationToken ct,
        string root = "/")
    {
        var ls = new HostedListings(files, root);
        var tmp = await ls.FileAsync(pending.Tmp, ct).ConfigureAwait(false);
        var target = await ls.FileAsync(pending.Target, ct).ConfigureAwait(false);
        var removed = pending.Removed.Select(r => r.Path).ToHashSet(StringComparer.Ordinal);

        bool forward;
        if (tmp is null)
        {
            // временного файла нет: переименование успело (назначение на месте) — или файл кто-то убрал
            forward = target is not null;
            if (!forward) await RestoreAsync(files, ls, pending, ct).ConfigureAwait(false);
        }
        else if (tmp.Size == pending.Size && (target is null || removed.Contains(target.Path))
                 && pending.Removed.All(r => File.Exists(r.Local)))
        {
            // залит целиком, назначение свободно или занято заменяемой версией — доводим: старое убрать, новое на место
            foreach (var old in pending.Removed)
                if (await ls.FileAsync(old.Path, ct).ConfigureAwait(false) is not null)
                    await files.DeleteFileAsync(old.Path, ct).ConfigureAwait(false);
            await files.RenameAsync(pending.Tmp, pending.Target, ct).ConfigureAwait(false);
            forward = true;
        }
        else
        {
            await RollBackAsync(files, pending, ct, root).ConfigureAwait(false);
            forward = false;
        }

        if (forward) KeepOld(profileId, pending);
        Delete(profileId, pending);
        return forward;
    }

    /// <summary>
    /// Вернуть как было: временный файл убрать, пропавшие старые версии залить из копий. false — возвращать нечего:
    /// временного файла нет, а назначение на месте, то есть переименование прошло (ответ сервера потерялся). Старые
    /// версии тогда не заливаются — иначе в папке модов оказались бы обе.
    /// </summary>
    public static async Task<bool> RollBackAsync(IRemoteFiles files, HostedPending pending, CancellationToken ct, string root = "/")
    {
        var ls = new HostedListings(files, root);
        if (await ls.FileAsync(pending.Tmp, ct).ConfigureAwait(false) is not null)
            await files.DeleteFileAsync(pending.Tmp, ct).ConfigureAwait(false);
        else if (await ls.FileAsync(pending.Target, ct).ConfigureAwait(false) is not null)
            return false;
        await RestoreAsync(files, ls, pending, ct).ConfigureAwait(false);
        return true;
    }

    private static async Task RestoreAsync(IRemoteFiles files, HostedListings ls, HostedPending pending, CancellationToken ct)
    {
        foreach (var old in pending.Removed)
        {
            if (await ls.FileAsync(old.Path, ct).ConfigureAwait(false) is not null || !File.Exists(old.Local)) continue;
            await using var source = File.OpenRead(old.Local);
            await files.UploadAsync(source, old.Path, null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Копии заменённых версий — в хранилище версий (для отката), изменение — в историю. Сбой на этой машине (диск,
    /// права) не роняет вызывающего: на сервере всё уже сделано, а без этой страховки каждый следующий скан снова
    /// упирался бы в тот же сбой и профиль бы не открывался.
    /// </summary>
    internal static void KeepOld(string profileId, HostedPending pending)
    {
        var store = ModBackupStore.ForProfileId(profileId);
        foreach (var old in pending.Removed.Where(r => File.Exists(r.Local)))
            Quietly(() => store.Keep(ModScanner.ReadZip(old.Local)));
        Quietly(() => ModHistory.Record(ModHistory.FileFor(profileId), pending.ModId, pending.Name, pending.From, pending.To));
    }

    // ---------- конфиг через bak

    public const string ConfigSwapFileName = "config-swap.txt";

    /// <summary>
    /// Отметка «конфиг сейчас лежит на сервере как serverconfig.json.evistool.bak»: ставится перед переименованием
    /// конфига в bak и снимается, когда на его место встал новый (или вернулся прежний). Если сорвались оба
    /// переименования, следующий скан по ней вернёт bak на место. Отметка — подстраховка: не записалась, правка идёт
    /// и без неё.
    /// </summary>
    public static void MarkConfigSwap(string profileId, string configPath) =>
        Quietly(() =>
        {
            Directory.CreateDirectory(DirFor(profileId));
            File.WriteAllText(Path.Combine(DirFor(profileId), ConfigSwapFileName), configPath);
        });

    /// <summary>Путь конфига, ушедшего в bak (отметка есть) — или null.</summary>
    public static string? ConfigSwap(string profileId)
    {
        try
        {
            var file = Path.Combine(DirFor(profileId), ConfigSwapFileName);
            return File.Exists(file) ? RemotePath.Normalize(File.ReadAllText(file).Trim()) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null; // битая отметка — как нет
        }
    }

    public static void ClearConfigSwap(string profileId) =>
        Quietly(() => File.Delete(Path.Combine(DirFor(profileId), ConfigSwapFileName)));

    private static void Quietly(Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // подстраховка на этой машине; основное дело уже сделано на сервере
        }
    }
}

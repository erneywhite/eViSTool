using eViSTool.Core.Profiles;
using eViSTool.Core.Versioning;
using eViSTool.Core.Localization;

namespace eViSTool.Core.Mods;

/// <summary>Что произойдёт при установке zip: новый мод, замена, даунгрейд или та же версия.</summary>
public sealed record InstallPlan
{
    public required string SourcePath { get; init; }
    public required LocalMod Incoming { get; init; }

    /// <summary>Уже установленные копии этого мода (обычно одна) — будут убраны в бэкап.</summary>
    public IReadOnlyList<LocalMod> Replaces { get; init; } = [];

    public required string TargetPath { get; init; }

    public bool IsReplace => Replaces.Count > 0;
    public bool IsSameVersion { get; init; }
    public bool IsDowngrade { get; init; }

    /// <summary>Зависимости, которых нет среди установленных модов.</summary>
    public IReadOnlyList<string> MissingDependencies { get; init; } = [];

    /// <summary>Версия игры профиля (null — неизвестна).</summary>
    public ModVersion? Game { get; init; }

    /// <summary>Мод требует игру новее, чем у профиля, — какую; иначе null. Игра такой мод не загрузит.</summary>
    public ModVersion? NeedsGame => Incoming.Info?.NeedsNewerGame(Game);
}

public static class ModInstaller
{
    /// <summary>Разбирает zip и решает, куда и что ставить. Ничего не меняет на диске.</summary>
    public static InstallPlan Plan(string sourceZip, ResolvedProfile profile, IReadOnlyList<LocalMod> installed)
    {
        var incoming = ModScanner.ReadZip(sourceZip);
        if (incoming.Info is null)
            throw new InvalidDataException($"{Path.GetFileName(sourceZip)}: {incoming.Error}");

        var info = incoming.Info;
        var replaces = installed
            .Where(m => m.Info is not null && m.Info.ModId == info.ModId
                        && !string.Equals(Path.GetFullPath(m.Path), Path.GetFullPath(sourceZip), StringComparison.OrdinalIgnoreCase))
            .ToList();

        // новая версия ложится туда же, где лежала старая; новый мод — в папку установки профиля
        var dir = replaces.FirstOrDefault()?.Directory ?? profile.InstallDir
                  ?? throw new InvalidOperationException(Loc.T("err.noModsDir"));
        var target = Path.Combine(dir, Path.GetFileName(sourceZip));

        // файл с таким именем есть, но это другой мод — не затираем его: ищем действительно свободное имя
        // (name.zip → name_modid.zip → name_modid_2.zip …)
        var stem = Path.GetFileNameWithoutExtension(sourceZip);
        var ext = Path.GetExtension(sourceZip);
        for (var n = 1; IsTaken(target, sourceZip, replaces); n++)
            target = Path.Combine(dir, n == 1 ? $"{stem}_{info.ModId}{ext}" : $"{stem}_{info.ModId}_{n}{ext}");

        var newVersion = ModVersion.ParseOrNull(info.Version);
        var oldVersion = replaces.Select(r => ModVersion.ParseOrNull(r.Info!.Version)).Where(v => v is not null).Max();

        var present = installed.Where(m => m.Info is not null).Select(m => m.Info!.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = info.Dependencies.Keys
            .Where(d => !ModInfo.IsBaseGame(d) && !present.Contains(d))
            .ToList();

        return new InstallPlan
        {
            SourcePath = sourceZip,
            Incoming = incoming,
            Replaces = replaces,
            TargetPath = target,
            IsSameVersion = newVersion is not null && oldVersion is not null && newVersion.CompareTo(oldVersion) == 0,
            IsDowngrade = newVersion is not null && oldVersion is not null && newVersion.CompareTo(oldVersion) < 0,
            MissingDependencies = missing,
            Game = profile.GameVersion,
        };
    }

    /// <summary>Ставит мод: старые копии — в хранилище бэкапов, новый zip — в папку модов.</summary>
    /// <summary>Имя занято чем-то, что эта установка не заменяет (другим модом или посторонним файлом).</summary>
    private static bool IsTaken(string target, string sourceZip, IReadOnlyList<LocalMod> replaces) =>
        (File.Exists(target) || Directory.Exists(target))
        && !SamePath(target, sourceZip)
        && !replaces.Any(r => SamePath(r.Path, target));

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ставит мод по плану. Перед записью ещё раз проверяет назначение: файл, появившийся после планирования,
    /// или заменяемый файл, в котором уже другой мод, не перезаписывается. При сбое прежние версии возвращаются на место.
    /// </summary>
    public static void Apply(InstallPlan plan, ModBackupStore backups)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(plan.TargetPath)!);
        if (IsTaken(plan.TargetPath, plan.SourcePath, plan.Replaces) || !StillSameMod(plan))
            throw new IOException(Loc.T("err.installTargetTaken", Path.GetFileName(plan.TargetPath)));

        // копируем во временный файл рядом, чтобы при ошибке не остаться без мода вовсе
        var tmp = plan.TargetPath + ".evistool.tmp";
        File.Copy(plan.SourcePath, tmp, overwrite: true);
        var kept = new List<(LocalMod Mod, string Stored)>();
        try
        {
            foreach (var old in plan.Replaces)
                kept.Add((old, backups.Keep(old)));
            // замены уже убраны в хранилище — назначение должно быть свободно; что-то появилось — не затираем
            File.Move(tmp, plan.TargetPath, overwrite: false);
        }
        catch
        {
            // вернуть прежние версии: без этого после сбоя мод пропал бы из папки совсем
            foreach (var (mod, stored) in kept)
            {
                if (File.Exists(mod.Path) || Directory.Exists(mod.Path)) continue;
                if (File.Exists(stored)) File.Move(stored, mod.Path);
                else if (Directory.Exists(stored)) Directory.Move(stored, mod.Path);
            }
            throw;
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }

        if (backups.HistoryFile is { } history && plan.Incoming.Info is { } info)
            ModHistory.Record(history, info.ModId, info.Name, plan.Replaces.FirstOrDefault()?.Info?.Version, info.Version);
    }

    /// <summary>Если назначение — заменяемый файл, в нём по-прежнему этот же мод (его не подменили после планирования).</summary>
    private static bool StillSameMod(InstallPlan plan)
    {
        if (!File.Exists(plan.TargetPath) || SamePath(plan.TargetPath, plan.SourcePath)) return true;
        var now = ModScanner.ReadZip(plan.TargetPath).Info?.ModId;
        return string.Equals(now, plan.Incoming.Info!.ModId, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Хранилище старых версий модов: &lt;папка данных&gt;\ModBackups\&lt;профиль&gt;\&lt;modid&gt;\.
/// Отсюда — откат (этап 2c). Держим несколько последних версий каждого мода.
/// </summary>
public sealed class ModBackupStore(string root, int keepPerMod = 3, string? historyFile = null)
{
    public string Root { get; } = root;

    /// <summary>Куда писать историю изменений модов этого профиля (null — не писать).</summary>
    public string? HistoryFile { get; } = historyFile;

    public static ModBackupStore ForProfile(GameProfile profile) => ForProfileId(profile.Id);

    public static ModBackupStore ForProfileId(string profileId) =>
        new(Path.Combine(AppPaths.ModBackups, profileId), historyFile: ModHistory.FileFor(profileId));

    public string DirFor(string modId) => Path.Combine(Root, modId);

    /// <summary>Переносит файл мода в хранилище (не копирует — из папки модов он должен исчезнуть).</summary>
    public string Keep(LocalMod mod)
    {
        var dir = DirFor(mod.Info?.ModId ?? "_unknown");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, mod.FileName);
        if (File.Exists(dest))
            dest = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(mod.FileName)}_{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(mod.FileName)}");

        if (File.Exists(mod.Path)) File.Move(mod.Path, dest);
        else if (Directory.Exists(mod.Path)) Directory.Move(mod.Path, dest);
        // время попадания в хранилище — по нему порядок и чистка (дата изменения у файла остаётся авторская)
        if (File.Exists(dest)) File.SetCreationTimeUtc(dest, DateTime.UtcNow);
        else Directory.SetCreationTimeUtc(dest, DateTime.UtcNow);

        Prune(dir);
        return dest;
    }

    public IReadOnlyList<string> List(string modId)
    {
        var dir = DirFor(modId);
        return Directory.Exists(dir)
            ? Directory.EnumerateFileSystemEntries(dir).OrderByDescending(File.GetCreationTimeUtc).ToList()
            : [];
    }

    private void Prune(string dir)
    {
        foreach (var old in Directory.EnumerateFiles(dir).OrderByDescending(File.GetCreationTimeUtc).Skip(keepPerMod))
            File.Delete(old);
    }
}

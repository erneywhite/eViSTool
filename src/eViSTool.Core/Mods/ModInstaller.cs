using eViSTool.Core.Profiles;
using eViSTool.Core.Versioning;

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
                  ?? throw new InvalidOperationException("У профиля нет папки модов");
        var target = Path.Combine(dir, Path.GetFileName(sourceZip));

        // файл с таким именем есть, но это другой мод — не затираем его
        var targetTaken = File.Exists(target)
            && !replaces.Any(r => string.Equals(Path.GetFullPath(r.Path), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            && !string.Equals(Path.GetFullPath(sourceZip), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        if (targetTaken)
            target = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(sourceZip)}_{info.ModId}{Path.GetExtension(sourceZip)}");

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
        };
    }

    /// <summary>Ставит мод: старые копии — в хранилище бэкапов, новый zip — в папку модов.</summary>
    public static void Apply(InstallPlan plan, ModBackupStore backups)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(plan.TargetPath)!);

        // копируем во временный файл рядом, чтобы при ошибке не остаться без мода вовсе
        var tmp = plan.TargetPath + ".evistool.tmp";
        File.Copy(plan.SourcePath, tmp, overwrite: true);
        try
        {
            foreach (var old in plan.Replaces)
                backups.Keep(old);
            File.Move(tmp, plan.TargetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}

/// <summary>
/// Хранилище старых версий модов: %LOCALAPPDATA%\eViSTool\ModBackups\&lt;профиль&gt;\&lt;modid&gt;\.
/// Отсюда — откат (этап 2c). Держим несколько последних версий каждого мода.
/// </summary>
public sealed class ModBackupStore(string root, int keepPerMod = 3)
{
    public string Root { get; } = root;

    public static ModBackupStore ForProfile(GameProfile profile) => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "eViSTool", "ModBackups", profile.Id));

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

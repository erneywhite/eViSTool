using eViSTool.Core.Localization;

namespace eViSTool.Core.Profiles;

/// <summary>Своя копия модов клона: что скопировать и что сказать пользователю перед копированием.</summary>
public sealed record CloneModsPlan(IReadOnlyList<CloneFile> Files, IReadOnlyList<string> Directories,
    IReadOnlyList<string> Sources, IReadOnlyList<string> Notes);

/// <summary>
/// Сборка «своей копии модов» для клона (серверного и клиентского): моды из всех папок, которые реально читает
/// исходный профиль (в том числе вне его папки данных), — в одну папку клона. Ничего не пропадает молча:
/// одинаковый мод из нескольких папок копируется один раз, разные с одним именем — оба (второй под «имя (2)»),
/// и об этом сказано в <see cref="CloneModsPlan.Notes"/>; отсутствующие папки тоже перечислены.
/// </summary>
public static class CloneMods
{
    public static CloneModsPlan Collect(IEnumerable<string> modDirs, string targetRelative)
    {
        var files = new List<CloneFile>();
        var dirs = new List<string> { targetRelative };
        var sources = new List<string>();
        var notes = new List<string>();
        var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // имя в клоне → откуда

        foreach (var dir in modDirs.Select(d => Path.GetFullPath(d).TrimEnd('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir))
            {
                notes.Add(Loc.T("clone.modsMissing", dir));
                continue;
            }
            sources.Add(dir);
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir).OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(entry);
                if (taken.TryGetValue(name, out var first))
                {
                    if (SameContent(first, entry))
                    {
                        notes.Add(Loc.T("clone.modsDuplicate", name));
                        continue;
                    }
                    var renamed = FreeName(name, taken, Directory.Exists(entry));
                    notes.Add(Loc.T("clone.modsRenamed", name, dir, renamed));
                    name = renamed;
                }
                taken[name] = entry;
                var rel = Path.Combine(targetRelative, name);
                if (Directory.Exists(entry))
                {
                    dirs.Add(rel);
                    dirs.AddRange(Directory.EnumerateDirectories(entry, "*", SearchOption.AllDirectories)
                        .Select(d => Path.Combine(rel, Path.GetRelativePath(entry, d))));
                    foreach (var f in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories))
                        files.Add(new CloneFile(f, Path.Combine(rel, Path.GetRelativePath(entry, f)), new FileInfo(f).Length));
                }
                else files.Add(new CloneFile(entry, rel, new FileInfo(entry).Length));
            }
        }
        return new CloneModsPlan(files, dirs, sources, notes);
    }

    /// <summary>«carryon.zip» → «carryon (2).zip», папка «mod» → «mod (2)»: первое свободное.</summary>
    private static string FreeName(string name, IReadOnlyDictionary<string, string> taken, bool isDir)
    {
        var stem = isDir ? name : Path.GetFileNameWithoutExtension(name);
        var ext = isDir ? "" : Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!taken.ContainsKey(candidate)) return candidate;
        }
    }

    /// <summary>Тот же мод: файл — те же байты; папка — те же файлы с теми же байтами.</summary>
    private static bool SameContent(string a, string b)
    {
        if (File.Exists(a) && File.Exists(b)) return SameFile(a, b);
        if (!Directory.Exists(a) || !Directory.Exists(b)) return false;
        var left = Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(a, f))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        var right = Directory.EnumerateFiles(b, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(b, f))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        return left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase)
               && left.All(rel => SameFile(Path.Combine(a, rel), Path.Combine(b, rel)));
    }

    private static bool SameFile(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var x = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var y = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> bx = stackalloc byte[8192], by = stackalloc byte[8192];
        while (true)
        {
            var n = x.ReadAtLeast(bx, bx.Length, throwOnEndOfStream: false);
            var m = y.ReadAtLeast(by, by.Length, throwOnEndOfStream: false);
            if (n != m || !bx[..n].SequenceEqual(by[..m])) return false;
            if (n == 0) return true;
        }
    }
}

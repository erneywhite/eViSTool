using eViSTool.Core.Localization;

namespace eViSTool.Core.Server;

/// <summary>Мир сервера: файл в Saves — имя без «.vcdbs», полный путь на машине сервера, размер, когда менялся.</summary>
public sealed record ServerWorld(string Name, string Path, long Size, DateTime ModifiedUtc);

/// <summary>Миры в папке Saves сервера и сама папка (полный путь на машине сервера: новый мир ляжет в неё).</summary>
public sealed record ServerWorldList(string SavesDir, IReadOnlyList<ServerWorld> Worlds);

/// <summary>
/// Миры выделенного сервера. Каждый мир — свой файл Saves/имя.vcdbs, а какой запускать, сервер берёт из
/// WorldConfig.SaveFileLocation. Сид, стиль игры и прочие параметры создания записываются в файл мира, поэтому один
/// serverconfig.json служит всем мирам: у существующего мира эти поля конфига не читаются. Новый мир — путь к файлу,
/// которого ещё нет: сервер создаст его при запуске, а прежние миры остаются в Saves как были.
/// </summary>
public static class ServerWorlds
{
    public const string SavesDir = "Saves";
    public const string Extension = ".vcdbs";

    /// <summary>Миры папки данных: сначала недавние. Папки Saves ещё нет — пусто.</summary>
    public static ServerWorldList List(string dataDir)
    {
        var saves = System.IO.Path.GetFullPath(System.IO.Path.Combine(dataDir, SavesDir));
        if (!Directory.Exists(saves)) return new ServerWorldList(saves, []);
        return new ServerWorldList(saves, [.. new DirectoryInfo(saves).EnumerateFiles("*" + Extension)
            .Select(f => new ServerWorld(System.IO.Path.GetFileNameWithoutExtension(f.Name), f.FullName, f.Length, f.LastWriteTimeUtc))
            .OrderByDescending(w => w.ModifiedUtc)]);
    }

    /// <summary>
    /// Путь к файлу нового мира с таким именем — в папке Saves сервера, с разделителем её системы (сервер может быть
    /// на Linux). Пустое имя, имя с путём или запрещёнными знаками и имя существующего мира — ошибка словами для окна.
    /// </summary>
    public static (string? Path, string? Error) NewWorldPath(ServerWorldList list, string? name)
    {
        name = name?.Trim() ?? "";
        if (name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) name = name[..^Extension.Length].TrimEnd();
        if (name.Length == 0) return (null, Loc.T("srvcfg.newWorldEmpty"));
        if (name is "." or ".." || name.Length > 100 || name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) || name.EndsWith('.'))
            return (null, Loc.T("srvcfg.newWorldBadName", name));
        if (list.Worlds.Any(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)))
            return (null, Loc.T("srvcfg.newWorldExists", name));
        var separator = list.SavesDir.Contains('\\') && !list.SavesDir.Contains('/') ? '\\' : list.SavesDir.Contains('/') && !list.SavesDir.Contains('\\') ? '/' : System.IO.Path.DirectorySeparatorChar;
        return (list.SavesDir.TrimEnd('/', '\\') + separator + name + Extension, null);
    }

    /// <summary>
    /// Полный путь мира из WorldConfig.SaveFileLocation: относительный — от папки данных сервера (родитель Saves). Для
    /// сравнения с путями списка: разделители и регистр не в счёт (сервер на Windows их не различает).
    /// </summary>
    public static string Resolve(ServerWorldList list, string saveFileLocation)
    {
        var path = saveFileLocation.Trim();
        var rooted = path.StartsWith('/') || path.StartsWith('\\') || (path.Length >= 2 && path[1] == ':');
        if (rooted || path.Length == 0) return path;
        var dataDir = list.SavesDir.TrimEnd('/', '\\');
        var cut = dataDir.LastIndexOfAny(['/', '\\']);
        return (cut > 0 ? dataDir[..(cut + 1)] : "") + path;
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(a.Replace('\\', '/').TrimEnd('/'), b.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Мир из списка по значению SaveFileLocation; null — такого файла нет (мир будет создан при запуске).</summary>
    public static ServerWorld? Find(ServerWorldList list, string saveFileLocation)
    {
        var full = Resolve(list, saveFileLocation);
        return list.Worlds.FirstOrDefault(w => SamePath(w.Path, full));
    }
}

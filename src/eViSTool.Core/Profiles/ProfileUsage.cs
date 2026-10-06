using eViSTool.Core.Localization;
using eViSTool.Core.Server;

namespace eViSTool.Core.Profiles;

public enum ProfileUseKind { Data, Mods, World }

/// <summary>Другой профиль пользуется чем-то в папке: своей папкой данных, папкой модов или файлом мира.</summary>
public sealed record ProfileDependency(GameProfile Profile, ProfileUseKind Kind, string Path)
{
    public string Describe() => Kind switch
    {
        ProfileUseKind.Data => Loc.T("usage.data", Profile.Name, Path),
        ProfileUseKind.Mods => Loc.T("usage.mods", Profile.Name, Path),
        _ => Loc.T("usage.world", Profile.Name, Path),
    };
}

/// <summary>
/// Кто ещё пользуется папкой — перед тем как предложить убрать её вместе с профилем. Смотрит не только на папки
/// данных других профилей, но и на то, что они реально читают: папки модов (общие моды клона ведут в папку
/// исходного профиля) и файл мира сервера, лежащий вне его папки данных.
/// </summary>
public static class ProfileUsage
{
    public static IReadOnlyList<ProfileDependency> UsersOf(string dir, IEnumerable<GameProfile> others)
    {
        var found = new List<ProfileDependency>();
        foreach (var p in others)
        {
            if (p.IsRemote || string.IsNullOrWhiteSpace(p.DataDir)) continue; // удалённый живёт на другой машине
            if (IsSameOrInside(p.DataDir, dir))
            {
                found.Add(new ProfileDependency(p, ProfileUseKind.Data, Full(p.DataDir)));
                continue;
            }

            foreach (var mods in ProfileResolver.Resolve(p).ModDirs.Where(m => IsSameOrInside(m, dir)))
                found.Add(new ProfileDependency(p, ProfileUseKind.Mods, Full(mods)));

            if (p.Kind == ProfileKind.Server && Directory.Exists(p.DataDir) && SaveOf(p.DataDir) is { } save && IsSameOrInside(save, dir))
                found.Add(new ProfileDependency(p, ProfileUseKind.World, Full(save)));
        }
        return found;
    }

    private static string? SaveOf(string dataDir)
    {
        try
        {
            return ServerFiles.SaveFileOf(dataDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            return null; // конфиг не прочитать — сервер с ним и не запустится
        }
    }

    /// <summary>Путь — сама папка или внутри неё («A» и «AB» — разные папки; регистр и слэш в конце не важны).</summary>
    public static bool IsSameOrInside(string? path, string dir)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(dir)) return false;
        var p = Full(path);
        var d = Full(dir);
        return p.Equals(d, StringComparison.OrdinalIgnoreCase) || p.StartsWith(d + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Full(string path) => System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
}

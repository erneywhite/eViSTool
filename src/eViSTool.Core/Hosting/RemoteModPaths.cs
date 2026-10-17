using eViSTool.Core.Mods;

namespace eViSTool.Core.Hosting;

/// <summary>
/// Пути модов сервера у хостера для <see cref="ModInstaller.Plan"/>: склейка через «/», а «занято ли имя» — по листингу
/// папок, а не по диску этой машины. Регистр важен: сервер обычно на Linux.
/// </summary>
public sealed class RemoteModPaths(IEnumerable<RemoteEntry> listing) : IModPaths
{
    private readonly HashSet<string> _present = listing.Select(e => RemotePath.Normalize(e.Path)).ToHashSet(StringComparer.Ordinal);

    public string Combine(string dir, string name) => RemotePath.Combine(dir, name);

    public string DirectoryOf(string path) => RemotePath.Parent(path);

    public string FileName(string path) => RemotePath.Name(path);

    public bool Exists(string path) => _present.Contains(RemotePath.Normalize(path));

    public bool Same(string a, string b) => string.Equals(RemotePath.Normalize(a), RemotePath.Normalize(b), StringComparison.Ordinal);
}

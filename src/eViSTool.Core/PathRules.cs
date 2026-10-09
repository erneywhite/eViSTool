namespace eViSTool.Core;

/// <summary>
/// Пути и имена файлов, одинаковые для окна на Windows и агента на Linux. Агент принимает от окна пути внутри своих
/// папок — выход наружу должен отсекаться на любой системе; имена, которые придумывает eViSTool (копии мира, папки
/// профилей), из одного названия получаются одни и те же.
/// </summary>
public static class PathRules
{
    /// <summary>
    /// Как сравнивать пути этой машины: на Windows регистр не важен, на Linux «Mods» и «mods» — разные папки,
    /// и проверка «внутри папки» без учёта регистра пропустила бы соседнюю.
    /// </summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Знак, которого не бывает в имени файла на Windows: «\ / : * ? " &lt; &gt; |» и управляющие. Linux запрещает
    /// только «/» и нулевой, но имя по его правилам не перенести на Windows и в сетевую папку, а окно на Windows
    /// получило бы из того же названия другое имя.
    /// </summary>
    public static bool IsBadInFileName(char c) => c < ' ' || c is '"' or '<' or '>' or '|' or ':' or '*' or '?' or '\\' or '/';

    /// <summary>
    /// Путь внутри папки, присланный окном («sub/x.json»), → полный путь на этой машине; выход за папку — null.
    /// «\» считается разделителем и на Linux: окно на Windows пишет пути с ним, и «..\x» должно значить то же, что там.
    /// Путь от корня («/etc/x», «\\сервер\x») и с диском Windows («C:/x») не принимается ни на какой системе.
    /// </summary>
    public static string? Inside(string root, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath.Contains('\0')) return null;
        var rel = relativePath.Replace('\\', '/');
        if (rel.StartsWith('/') || (rel.Length >= 2 && rel[1] == ':')) return null;
        var dir = Path.GetFullPath(root);
        if (!Path.EndsInDirectorySeparator(dir)) dir += Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)));
        return full.Length > dir.Length && full.StartsWith(dir, Comparison) ? full : null;
    }
}

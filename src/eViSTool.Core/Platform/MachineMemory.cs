using System.Globalization;

namespace eViSTool.Core.Platform;

/// <summary>
/// Сколько памяти есть у процесса сервера: вся память машины, а в контейнере или юните systemd с пределом — предел.
/// На Linux предел читается из cgroup напрямую: .NET в контейнере отдаёт в <see cref="GCMemoryInfo.TotalAvailableMemoryBytes"/>
/// 75 % предела (столько он разрешает своей куче), и при MemoryMax=1G окно показало бы «из 768 МБ».
/// </summary>
public static class MachineMemory
{
    /// <summary>Память в МБ; null — узнать не вышло.</summary>
    public static long? TotalMb()
    {
        if (OperatingSystem.IsLinux() && Linux("/") is { } bytes) return bytes / (1024 * 1024);
        var gc = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (gc <= 0)
        {
            GC.Collect(0); // сведения берутся из последней сборки мусора; её могло ещё не быть
            gc = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }
        return gc > 0 ? gc / (1024 * 1024) : null;
    }

    /// <summary>
    /// Linux: MemTotal из /proc/meminfo, урезанный пределами cgroup своей группы и всех родительских (v2: memory.max,
    /// v1: memory.limit_in_bytes). <paramref name="root"/> — корень файловой системы (тесты подставляют свою папку).
    /// null — /proc/meminfo не прочитать.
    /// </summary>
    public static long? Linux(string root)
    {
        try
        {
            if (MemTotal(Read(root, "proc/meminfo")) is not { } total) return null;
            var limit = long.MaxValue;
            foreach (var line in Read(root, "proc/self/cgroup")?.Split('\n') ?? [])
            {
                // «0::/system.slice/evistool.service» (v2) или «7:memory:/docker/abc» (v1)
                var parts = line.Trim().Split(':', 3);
                if (parts.Length != 3) continue;
                var (controllers, path) = (parts[1], parts[2]);
                if (controllers.Length == 0)
                    limit = Math.Min(limit, Walk(root, "sys/fs/cgroup", path, "memory.max"));
                else if (controllers.Split(',').Contains("memory"))
                    limit = Math.Min(limit, Walk(root, "sys/fs/cgroup/memory", path, "memory.limit_in_bytes"));
            }
            return Math.Min(total, limit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // предел группы — наименьший по пути от неё до корня: его может задать и родитель (slice)
    private static long Walk(string root, string mount, string path, string file)
    {
        var limit = long.MaxValue;
        var dir = path.TrimEnd('/');
        while (true)
        {
            if (Read(root, $"{mount}{dir}/{file}")?.Trim() is { } text
                && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0)
                limit = Math.Min(limit, value); // «max» (v2) не число — предела нет; огромное число v1 — тоже фактически нет
            if (dir.Length == 0) return limit;
            dir = dir[..dir.LastIndexOf('/')];
        }
    }

    private static long? MemTotal(string? meminfo)
    {
        // «MemTotal:        4009820 kB»
        var line = meminfo?.Split('\n').FirstOrDefault(l => l.StartsWith("MemTotal:", StringComparison.Ordinal));
        var number = line?["MemTotal:".Length..].Trim().Split(' ')[0];
        return long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var kb) ? kb * 1024 : null;
    }

    private static string? Read(string root, string relative)
    {
        var path = Path.Combine(root, relative);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}

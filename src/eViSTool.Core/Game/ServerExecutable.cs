using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using eViSTool.Core.Localization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Game;

/// <summary>
/// Файл сервера VS и как его запускать на этой платформе. На Windows сервер — VintagestoryServer.exe. На Linux —
/// VintagestoryServer.dll, её запускает dotnet, как и официальный server.sh. Рядом лежит и запускатель VintagestoryServer
/// без расширения, но .NET он ищет по своим правилам и без DOTNET_ROOT может не найти, поэтому идём через dotnet сами.
/// </summary>
public static class ServerExecutable
{
    /// <summary>Имя процесса сервера, запущенного напрямую (exe на Windows, запускатель на Linux).</summary>
    public const string ProcessName = "VintagestoryServer";
    public const string ExeName = "VintagestoryServer.exe";
    public const string DllName = "VintagestoryServer.dll";

    /// <summary>Файл сервера в папке игры на этой платформе.</summary>
    public static string FileName => OperatingSystem.IsWindows() ? ExeName : DllName;

    public static string PathIn(string? gameDir) => Path.Combine(gameDir ?? "", FileName);

    /// <summary>Какая среда .NET нужна сборке: имя, наименьшая версия и можно ли взять более новую старшую версию.</summary>
    public sealed record Runtime(string Name, Version Version, bool AnyNewerMajor);

    /// <summary>
    /// С чем запускать сервер: .dll — через dotnet, остальное — напрямую; рабочая папка — папка сервера. Аргументы сервера
    /// (--dataPath и прочие) добавляет вызывающий. Бросает InvalidOperationException с понятным текстом, если нужного
    /// серверу .NET нет или файл — не сборка .NET.
    /// </summary>
    public static ProcessStartInfo StartInfo(string serverFile)
    {
        var path = Path.GetFullPath(serverFile);
        // на Linux указали запускатель без расширения — берём его dll, чтобы не зависеть от того, где он ищет .NET
        if (!OperatingSystem.IsWindows() && Path.GetExtension(path).Length == 0 && File.Exists(path + ".dll")) path += ".dll";

        ProcessStartInfo psi;
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            CheckAssembly(path);
            var need = RequiredRuntime(path);
            var dotnet = FindDotnet();
            if (dotnet is null || (need is not null && !HasRuntime(dotnet, need)))
                throw new InvalidOperationException(Loc.T("srv.dotnetMissing", need?.Version.Major ?? 10));
            psi = new ProcessStartInfo(dotnet);
            psi.ArgumentList.Add(path);
        }
        else
        {
            psi = new ProcessStartInfo(path);
        }
        psi.WorkingDirectory = Path.GetDirectoryName(path)!;
        psi.UseShellExecute = false;
        return psi;
    }

    /// <summary>
    /// Битую или чужую dll dotnet всё равно запустит, она тут же упадёт, а сторож будет поднимать её снова и снова.
    /// Проверяем заранее: заголовок сборки читается быстро.
    /// </summary>
    private static void CheckAssembly(string dll)
    {
        try
        {
            AssemblyName.GetAssemblyName(dll);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
        {
            throw new InvalidOperationException(Loc.T("srv.badServerFile", dll), ex);
        }
    }

    /// <summary>
    /// Где dotnet: DOTNET_ROOT, PATH, место, записанное установщиком (/etc/dotnet/install_location), и обычные папки.
    /// У службы systemd PATH короткий, а dotnet из скрипта Microsoft лежит в ~/.dotnet — поэтому смотрим не только PATH.
    /// null — не нашли.
    /// </summary>
    public static string? FindDotnet()
    {
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var arch = RuntimeInformation.OSArchitecture.ToString().ToUpperInvariant();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        List<string?> dirs =
        [
            Environment.GetEnvironmentVariable("DOTNET_ROOT_" + arch),
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            .. (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator),
        ];
        if (OperatingSystem.IsWindows())
        {
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
        }
        else
        {
            dirs.Add(ReadInstallLocation("/etc/dotnet/install_location_" + arch.ToLowerInvariant()));
            dirs.Add(ReadInstallLocation("/etc/dotnet/install_location"));
            dirs.AddRange(["/usr/lib/dotnet", "/usr/share/dotnet", "/usr/local/share/dotnet", "/opt/dotnet"]);
            if (!string.IsNullOrEmpty(home)) dirs.Add(Path.Combine(home, ".dotnet"));
        }

        foreach (var dir in dirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { } // мусор в PATH
        }
        return null;
    }

    private static string? ReadInstallLocation(string file)
    {
        try { return File.Exists(file) ? File.ReadLines(file).FirstOrDefault()?.Trim() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Какой .NET нужен сборке — из её runtimeconfig.json; null — не указано или не прочитать (пусть решает dotnet).</summary>
    public static Runtime? RequiredRuntime(string dll)
    {
        var config = Path.ChangeExtension(dll, ".runtimeconfig.json");
        try
        {
            if (!File.Exists(config)) return null;
            var options = JObject.Parse(File.ReadAllText(config))["runtimeOptions"];
            var framework = options?["framework"] as JObject
                            ?? (options?["frameworks"] as JArray)?.OfType<JObject>()
                                .FirstOrDefault(f => (string?)f["name"] == "Microsoft.NETCore.App");
            if ((string?)framework?["name"] is not { } fx || !Version.TryParse((string?)framework["version"], out var version)) return null;
            // по умолчанию .NET подбирает только ту же старшую версию; Major/LatestMajor разрешают и более новые
            var roll = (string?)options?["rollForward"];
            var anyMajor = roll is not null && roll.Contains("Major", StringComparison.OrdinalIgnoreCase);
            return new Runtime(fx, version, anyMajor);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Есть ли у этого dotnet нужная среда: dotnet ищет её только в своей папке, в shared/&lt;имя&gt;/&lt;версия&gt;.</summary>
    public static bool HasRuntime(string dotnet, Runtime need)
    {
        try
        {
            // /usr/bin/dotnet обычно ссылка на /usr/lib/dotnet/dotnet — среды лежат рядом с настоящим файлом
            var real = new FileInfo(dotnet).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? dotnet;
            var shared = Path.Combine(Path.GetDirectoryName(real)!, "shared", need.Name);
            if (!Directory.Exists(shared)) return false;
            foreach (var dir in Directory.EnumerateDirectories(shared))
            {
                var text = Path.GetFileName(dir).Split('-')[0]; // 10.0.0-rc.1… — предварительная, но тоже годится
                if (!Version.TryParse(text, out var have)) continue;
                if (have >= need.Version && (have.Major == need.Version.Major || need.AnyNewerMajor)) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

using System.Text;

namespace eViSTool.Desktop.Platform;

/// <summary>Отчёты об ошибках — в logs папки данных, по файлу на день (как у окна на WPF).</summary>
internal static class CrashLog
{
    public static string? Write(Exception? ex)
    {
        if (ex is null) return null;
        try
        {
            Directory.CreateDirectory(Core.AppPaths.Logs);
            var path = Path.Combine(Core.AppPaths.Logs, $"error-{DateTime.Now:yyyy-MM-dd}.log");
            File.AppendAllText(path, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====\n{ex}\n\n", Encoding.UTF8);
            return path;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

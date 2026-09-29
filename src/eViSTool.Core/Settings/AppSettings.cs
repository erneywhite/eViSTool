using eViSTool.Core.Game;
using Newtonsoft.Json;

namespace eViSTool.Core.Settings;

public sealed class AppSettings
{
    /// <summary>Папка игры или сервера (там, где Vintagestory.exe / VintagestoryServer.exe).</summary>
    public string? GameDir { get; set; }

    /// <summary>Папка модов, с которой работаем.</summary>
    public string ModsDir { get; set; } = GameInstall.DefaultModsDir;

    /// <summary>Предлагать пре-релизы модов (rc/pre) даже тем, у кого стоит стабильная версия.</summary>
    public bool AllowUnstable { get; set; }
}

/// <summary>Хранит настройки в %APPDATA%\eViSTool\settings.json. Запись атомарная: сначала во временный файл.</summary>
public sealed class SettingsStore(string? path = null)
{
    public string Path { get; } = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "eViSTool", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // битый файл не должен ронять приложение — сохраним копию и начнём с чистых настроек
            try { File.Copy(Path, Path + ".broken", overwrite: true); } catch (IOException) { }
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(settings, Formatting.Indented));
        File.Move(tmp, Path, overwrite: true);
    }
}

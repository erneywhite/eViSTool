using System.Text;
using eViSTool.Core.Platform;

namespace eViSTool.Desktop.Platform;

/// <summary>
/// Ярлык в меню приложений Linux: ~/.local/share/applications/evistool.desktop и значок рядом. Пишется при каждом
/// запуске, если изменился (папку с программой перенесли — ярлык ведёт на новое место). Без него программу из архива
/// приходится запускать из файлового менеджера, а в панели задач у окна нет значка.
/// </summary>
internal static class LinuxDesktopEntry
{
    public const string FileName = "evistool.desktop";

    public static void Ensure()
    {
        if (!OperatingSystem.IsLinux() || XdgDirs.DataHome() is not { } dataHome) return;
        if (Environment.ProcessPath is not { } exe) return;
        try
        {
            var icon = Path.Combine(dataHome, "icons", "hicolor", "256x256", "apps", "evistool.png");
            WriteIfChanged(icon, ReadLogo());
            WriteIfChanged(Path.Combine(dataHome, "applications", FileName), Encoding.UTF8.GetBytes(Content(exe, icon)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write(ex); // без ярлыка программа работает, просто её нет в меню
        }
    }

    /// <summary>Текст ярлыка. Путь к программе — в кавычках, со всеми экранированиями из спецификации Desktop Entry.</summary>
    public static string Content(string exe, string icon) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=eViSTool
        Comment={Core.Localization.Loc.T("desktop.comment")}
        Exec={Quote(exe)}
        Icon={icon}
        Terminal=false
        Categories=Game;Utility;
        StartupWMClass=eViSTool

        """.ReplaceLineEndings("\n");

    // внутри кавычек экранируются " ` $ \, а затем весь текст — ещё раз как строка (\ → \\); % — поле подстановки
    public static string Quote(string path)
    {
        var inner = new StringBuilder();
        foreach (var c in path)
        {
            if (c is '"' or '`' or '$' or '\\') inner.Append('\\');
            inner.Append(c);
        }
        return "\"" + inner.ToString().Replace("\\", "\\\\").Replace("%", "%%") + "\"";
    }

    private static byte[] ReadLogo()
    {
        using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://eViSTool/Assets/logo256.png"));
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static void WriteIfChanged(string path, byte[] content)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }
}

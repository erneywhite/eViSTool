using System.Text;

namespace eViSTool.Core.Platform;

/// <summary>
/// Корзина Linux по спецификации freedesktop.org (Trash 1.0): файл или папка переезжает в Trash/files, а в Trash/info
/// ложится .trashinfo с прежним путём и временем удаления. Такую корзину видят файловые менеджеры, а вернуть файл можно
/// и из консоли (<c>gio trash --restore</c>, <c>trash-restore</c>) или просто переносом обратно.
/// </summary>
public static class FreedesktopTrash
{
    /// <summary>
    /// В корзину пользователя ($XDG_DATA_HOME/Trash или ~/.local/share/Trash); домашней папки нет или туда нельзя
    /// писать (служебный пользователь) — в запасную папку trash в данных программы, устроенную так же.
    /// Возвращает, где файл теперь лежит.
    /// </summary>
    public static string Send(string path) => Send(path, Places(), DateTime.Now);

    private static IEnumerable<string> Places()
    {
        if (XdgDirs.DataHome() is { } home) yield return Path.Combine(home, "Trash");
        yield return Path.Combine(AppPaths.Root, "trash");
    }

    /// <param name="trashDirs">Корзины по порядку: файл ляжет в первую, куда можно писать.</param>
    public static string Send(string path, IEnumerable<string> trashDirs, DateTime now)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var isDir = Directory.Exists(full);
        if (!isDir && !File.Exists(full)) throw new FileNotFoundException(null, full);

        var slot = Reserve(full, trashDirs, now);
        bool copied;
        try
        {
            copied = MoveIn(full, slot.File, isDir);
        }
        catch
        {
            File.Delete(slot.Info); // не переехал — и записи о нём в корзине быть не должно
            throw;
        }
        // папка с другого раздела скопирована целиком; не удалится оригинал — ошибка, но копия в корзине уже полная
        if (copied) Directory.Delete(full, recursive: true);
        return slot.File;
    }

    /// <summary>Имя в корзине: files/имя и info/имя.trashinfo.</summary>
    private sealed record Slot(string File, string Info);

    /// <summary>
    /// Сначала, как велит спецификация, создаётся info-файл (CreateNew — имя занято за нами, даже если рядом удаляет
    /// кто-то ещё), потом уже переносится сам файл. Имя занято — «имя.2.zip», «имя.3.zip»…, как у GLib (Nautilus).
    /// </summary>
    private static Slot Reserve(string full, IEnumerable<string> trashDirs, DateTime now)
    {
        var name = Path.GetFileName(full);
        var (stem, ext) = (Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
        if (stem.Length == 0) (stem, ext) = (name, ""); // «.hidden» — без расширения
        var text = Encoding.UTF8.GetBytes($"[Trash Info]\nPath={Escape(full)}\nDeletionDate={now:yyyy-MM-ddTHH:mm:ss}\n");
        Exception? failure = null;
        foreach (var trash in trashDirs)
        {
            try
            {
                var files = Path.Combine(trash, "files");
                var info = Path.Combine(trash, "info");
                XdgDirs.CreatePrivate(files);
                XdgDirs.CreatePrivate(info);
                for (var i = 1; ; i++)
                {
                    var candidate = i == 1 ? name : $"{stem}.{i}{ext}";
                    var file = Path.Combine(files, candidate);
                    var infoFile = Path.Combine(info, candidate + ".trashinfo");
                    if (File.Exists(file) || Directory.Exists(file)) continue; // осталось без info — не трогаем
                    FileStream stream;
                    try
                    {
                        stream = new FileStream(infoFile, InfoOptions());
                    }
                    catch (IOException) when (File.Exists(infoFile))
                    {
                        continue;
                    }
                    try
                    {
                        using (stream) stream.Write(text);
                    }
                    catch
                    {
                        File.Delete(infoFile);
                        throw;
                    }
                    return new Slot(file, infoFile);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex; // сюда не пишется — следующая корзина
            }
        }
        throw failure ?? new IOException("no trash");
    }

    // в info — прежний путь: читать его только владельцу, как у GLib
    private static FileStreamOptions InfoOptions()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    /// <summary>Перенос внутрь корзины. true — папка была на другом разделе и скопирована, оригинал ещё на месте.</summary>
    private static bool MoveIn(string full, string target, bool isDir)
    {
        if (!isDir)
        {
            File.Move(full, target); // между разделами .NET сам копирует и удаляет
            return false;
        }
        try
        {
            Directory.Move(full, target);
            return false;
        }
        catch (IOException ex) when (ex.HResult == Exdev)
        {
            try
            {
                CopyDir(full, target);
            }
            catch
            {
                try { Directory.Delete(target, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                throw;
            }
            return true;
        }
    }

    // rename() между разделами: «Invalid cross-device link»
    private const int Exdev = 18;

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var dest = Path.Combine(to, Path.GetFileName(file));
            File.Copy(file, dest);
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(file));
        }
        foreach (var dir in Directory.EnumerateDirectories(from))
            CopyDir(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    /// <summary>Путь в .trashinfo — как в адресе: всё, кроме букв, цифр, «-._~» и «/», в %XX от UTF-8.</summary>
    public static string Escape(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}

using System.Text;

namespace eViSTool.Core.Platform;

/// <summary>
/// Запись секрета (ключ агента, настройки удалённого доступа, сертификат) так, чтобы на Linux его ни на миг не мог
/// прочитать другой пользователь машины. Файл сразу создаётся с правами rw------- (а не с правами по умолчанию и потом
/// chmod — между ними его успели бы прочитать) во временном файле рядом и атомарно встаёт на место: прежний файл с
/// более широкими правами заменяется целиком, а оборванная запись не оставляет полфайла.
/// </summary>
public static class OwnerOnlyFile
{
    public static void WriteText(string path, string text) => Write(path, Encoding.UTF8.GetBytes(text));

    public static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var stream = new FileStream(tmp, options)) stream.Write(bytes);
            SafeFile.Replace(tmp, path);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw;
        }
    }
}

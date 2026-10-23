namespace eViSTool.Core.Platform;

/// <summary>Запись файла целиком через временный: читатель видит либо старый файл, либо новый, но не половину.</summary>
public static class SafeFile
{
    /// <summary>
    /// Подменить <paramref name="target"/> готовым временным файлом. На Windows файл, который кто-то в этот миг читает
    /// (агент, второе окно, сама игра), подменить нельзя — File.Move падает с отказом в доступе. Чтение короткое, поэтому
    /// несколько попыток с паузой, а не ошибка «не удалось сохранить» (файл ненадолго держит и антивирус); не вышло
    /// за секунду — ошибка как есть.
    /// </summary>
    public static void Replace(string tmp, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 40)
            {
                Thread.Sleep(25);
            }
        }
    }

    /// <summary>
    /// Переименовать папку, пока в ней что-то держат открытым ненадолго. На Windows папку, в которой открыт хоть один файл,
    /// не переименовать — отказ в доступе. Только что скопированные файлы так держит антивирус, пока проверяет их (сотня
    /// архивов модов — секунды), поэтому попытки идут до <paramref name="patience"/>, а не ошибка сразу.
    /// </summary>
    public static async Task MoveDirectoryAsync(string from, string to, TimeSpan patience, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + patience;
        while (true)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && DateTime.UtcNow < until && Directory.Exists(from))
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
    }
}

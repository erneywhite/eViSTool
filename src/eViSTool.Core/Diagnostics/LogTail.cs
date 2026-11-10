using System.Text;

namespace eViSTool.Core.Diagnostics;

/// <summary>
/// Новые строки файла лога с прошлого чтения (как «tail -f»). Игра держит файл открытым и дописывает — читаем, не мешая.
/// Файл пересоздан (игра при запуске убирает прежний лог в архив) — читаем новый с начала. Недописанная последняя
/// строка ждёт следующего чтения.
/// </summary>
/// <remarks>
/// Смещение двигается ровно на прочитанные байты до последнего перевода строки: раньше оно ставилось на длину файла
/// после чтения, и то, что игра дописала за это время, терялось; а кусок, оборванный посреди буквы UTF-8, портил её.
/// За один раз читается не больше <see cref="MaxChunk"/> — огромный лог догоняется за несколько чтений.
/// </remarks>
public sealed class LogTail
{
    /// <summary>Сколько байт читать за раз.</summary>
    public const int MaxChunk = 4 << 20;

    private readonly string _path;
    private long _offset;
    private DateTime _created;

    /// <param name="fromStart">true — с начала файла; false — только то, что появится после создания.</param>
    public LogTail(string path, bool fromStart)
    {
        _path = path;
        _offset = fromStart || !File.Exists(path) ? 0 : new FileInfo(path).Length;
        _created = File.Exists(path) ? File.GetCreationTimeUtc(path) : default;
    }

    public string Path => _path;

    public IReadOnlyList<string> ReadNew() => Read(flush: false);

    /// <summary>Остаток, который ещё не закончился переводом строки (при закрытии игры — тоже её строка).</summary>
    public IReadOnlyList<string> Flush() => Read(flush: true);

    private IReadOnlyList<string> Read(bool flush)
    {
        try
        {
            if (!File.Exists(_path)) return [];
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var created = File.GetCreationTimeUtc(_path);
            if (created != _created)
            {
                // другой файл под тем же именем (прежний ушёл в архив) — читать с начала
                _created = created;
                _offset = 0;
            }
            if (stream.Length < _offset) _offset = 0; // файл начат заново — всё в нём новое

            var lines = new List<string>();
            while (true)
            {
                var available = stream.Length - _offset;
                if (available <= 0) break;
                var buffer = new byte[(int)Math.Min(available, MaxChunk)];
                stream.Position = _offset;
                var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                if (read == 0) break;
                var end = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
                // кусок без перевода строки: строка ещё дописывается — ждём; но строка длиннее куска или закрытие
                // игры — отдаём как есть
                var take = end >= 0 ? end + 1 : read == MaxChunk || flush ? read : 0;
                if (take == 0) break;
                Split(buffer.AsSpan(0, take), _offset == 0, lines);
                _offset += take;
                if (take < read && !flush) break; // за последним переводом строки — недописанная строка
            }
            return lines;
        }
        catch (IOException)
        {
            return []; // файл занят или пропал на миг — прочитаем в следующий раз
        }
    }

    private static void Split(ReadOnlySpan<byte> bytes, bool fileStart, List<string> into)
    {
        ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
        if (fileStart && bytes.StartsWith(bom)) bytes = bytes[3..]; // метка UTF-8 в начале файла
        var text = Encoding.UTF8.GetString(bytes);
        var parts = text.Split('\n');
        // последний кусок после завершающего перевода строки пуст; без перевода (Flush) — это и есть строка
        var count = parts[^1].Length == 0 ? parts.Length - 1 : parts.Length;
        for (var i = 0; i < count; i++) into.Add(parts[i].TrimEnd('\r'));
    }
}

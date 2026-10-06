using System.Text;

namespace eViSTool.Core.Diagnostics;

/// <summary>
/// Новые строки файла лога с прошлого чтения (как «tail -f»). Игра держит файл открытым и дописывает — читаем, не мешая.
/// Файл пересоздан (игра при запуске убирает прежний лог в архив) — читаем новый с начала. Недописанная последняя
/// строка ждёт следующего чтения.
/// </summary>
public sealed class LogTail
{
    private readonly string _path;
    private long _offset;
    private string _partial = "";
    private DateTime _created;

    /// <param name="fromStart">true — с начала файла; false — только то, что появится после создания.</param>
    public LogTail(string path, bool fromStart)
    {
        _path = path;
        _offset = fromStart || !File.Exists(path) ? 0 : new FileInfo(path).Length;
        _created = File.Exists(path) ? File.GetCreationTimeUtc(path) : default;
    }

    public string Path => _path;

    public IReadOnlyList<string> ReadNew()
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
                _partial = "";
            }
            if (stream.Length < _offset)
            {
                // файл начат заново — всё в нём новое
                _offset = 0;
                _partial = "";
            }
            if (stream.Length == _offset) return [];
            stream.Position = _offset;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = _partial + reader.ReadToEnd();
            _offset = stream.Length;
            var lines = text.Split('\n');
            _partial = lines[^1]; // без перевода строки в конце — строка ещё дописывается
            return [.. lines[..^1].Select(l => l.TrimEnd('\r'))];
        }
        catch (IOException)
        {
            return []; // файл занят или пропал на миг — прочитаем в следующий раз
        }
    }

    /// <summary>Остаток, который ещё не закончился переводом строки (при закрытии игры — тоже её строка).</summary>
    public IReadOnlyList<string> Flush()
    {
        var rest = ReadNew().ToList();
        if (_partial.Length > 0) rest.Add(_partial.TrimEnd('\r'));
        _partial = "";
        return rest;
    }
}

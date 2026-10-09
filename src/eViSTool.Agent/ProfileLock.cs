/// <summary>
/// Один агент на профиль. На Windows — именованный Mutex, как всегда (имя то же: копия после обновления ждёт прежнюю
/// именно по нему). На Linux именованный Mutex .NET виден только внутри своего сеанса (getsid): агент из systemd и агент,
/// запущенный руками по ssh, друг друга не заметили бы (проверено на Ubuntu 24.04). Там вместо него — монопольно
/// открытый файл «&lt;профиль&gt;.lock» рядом с файлом адреса (flock): его видят все процессы машины, и два eViSTool
/// в разных папках с одинаковым профилем друг другу не мешают. Процесс вышел или упал — замок снимает сама система.
/// </summary>
internal sealed class ProfileLock : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly FileStream? _file;

    private ProfileLock(Mutex? mutex, FileStream? file) => (_mutex, _file) = (mutex, file);

    /// <summary>Занять профиль; занят — подождать до wait (копия после обновления ждёт прежнюю). Не дождались — null.</summary>
    public static ProfileLock? Take(string profileId, string agentsDir, TimeSpan wait)
    {
        if (OperatingSystem.IsWindows())
        {
            var mutex = new Mutex(initiallyOwned: true, $"eViSTool.Agent.{profileId}", out var createdNew);
            if (!createdNew && wait > TimeSpan.Zero)
            {
                try { createdNew = mutex.WaitOne(wait); }
                catch (AbandonedMutexException) { createdNew = true; } // прежняя вышла, не отпустив, — профиль наш
            }
            if (createdNew) return new ProfileLock(mutex, null);
            mutex.Dispose();
            return null;
        }

        Directory.CreateDirectory(agentsDir);
        var path = Path.Combine(agentsDir, $"{profileId}.lock");
        var until = DateTime.Now + wait;
        while (true)
        {
            try
            {
                return new ProfileLock(null, new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (DateTime.Now < until)
            {
                Thread.Sleep(500);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    // Mutex не отпускаем явно: отпустить его может только поток, который занял, а после await код идёт в других.
    // Процесс выйдет — Mutex освободится сам (у ждущей копии — AbandonedMutexException, это учтено)
    public void Dispose()
    {
        _mutex?.Dispose();
        _file?.Dispose();
    }
}

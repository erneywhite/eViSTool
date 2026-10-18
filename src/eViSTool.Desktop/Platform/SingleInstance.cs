using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace eViSTool.Desktop.Platform;

/// <summary>
/// Одно окно eViSTool на папку данных. Первое окно держит файл-замок в папке данных и слушает именованный канал;
/// второй запуск из той же папки просит первое показаться и тихо выходит — новое окно не открывается, а уже открытое
/// выходит вперёд (несохранённые правки в нём не теряются). Портативная копия в другой папке — другая установка со
/// своими данными: у неё свой замок, она живёт рядом.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string ActivateMessage = "activate";

    private readonly FileStream? _lock;
    private readonly string _pipe;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Второй запуск попросил показаться — из фонового потока канала.</summary>
    public event Action? ActivateRequested;

    /// <summary>Замок взят: это окно — окно своей папки данных. false — открылись без замка (прежнее окно повисло).</summary>
    public bool IsOwner => _lock is not null;

    // без замка канал не слушаем: его держит повисшее окно, и просьбы показаться должны идти к нему, а не к нам
    private SingleInstance(FileStream? @lock, string pipe)
    {
        _lock = @lock;
        _pipe = pipe;
        if (@lock is not null) _ = Task.Run(ListenAsync);
    }

    /// <summary>
    /// Стать окном этой папки данных. null — окно уже открыто и попросили его показаться: этому процессу выйти.
    /// Замок занят, а канал молчит — прежнее окно как раз закрывается (перезапуск после обновления): ждём его до
    /// <paramref name="wait"/>. Не дождались (окно повисло) — открываемся без замка, чтобы человек не остался без окна:
    /// страхует сверка settings.json со вторым окном.
    /// </summary>
    public static SingleInstance? Start(string dataDir, TimeSpan wait)
    {
        Directory.CreateDirectory(dataDir);
        var pipe = PipeName(dataDir);
        var lockPath = Path.Combine(dataDir, "window.lock");
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            if (TryLock(lockPath) is { } held) return new SingleInstance(held, pipe);
            if (AskToActivate(pipe)) return null;
            if (DateTime.UtcNow >= until) return new SingleInstance(null, pipe);
            Thread.Sleep(200);
        }
    }

    /// <summary>Имя канала — от полного пути папки данных: у каждой установки своё.</summary>
    public static string PipeName(string dataDir)
    {
        var full = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) full = full.ToLowerInvariant(); // C:\A и c:\a — одна папка
        return "eViSTool-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16];
    }

    private static FileStream? TryLock(string path)
    {
        try
        {
            // FileShare.None: на Windows — запрет открытия, на Linux .NET берёт flock — оба отпускаются с процессом
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null; // держит другое окно
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Попросить открытое окно показаться. false — не ответило (закрывается или канала нет).</summary>
    private static bool AskToActivate(string pipe)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out);
            client.Connect(1000);
            // Windows не отдаёт фокус чужому фоновому процессу — разрешить его тому окну, раз человек запустил именно нас
            if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(AnyProcess);
            var bytes = Encoding.UTF8.GetBytes(ActivateMessage);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var message = await reader.ReadToEndAsync(_stop.Token).ConfigureAwait(false);
                if (message.Trim() == ActivateMessage) ActivateRequested?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // канал заняли или оборвали — подождать и слушать дальше: без канала второй запуск просто подождёт и откроется
                try { await Task.Delay(1000, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _lock?.Dispose();
    }

    private const int AnyProcess = -1; // ASFW_ANY

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}

using System.IO;
using eViSTool.Core.AppUpdate;
using eViSTool.Core.Server;
using eViSTool.Core.Versioning;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// eViSTool обновили на диске, пока окно открыто. Раз в 10 секунд — не лежит ли на месте exe этого окна версия новее
/// запущенной. Так бывает после «Обновить на сервере» с другого компьютера: агент подменил файлы и перезапустился, а
/// открытое здесь окно жило бы старой версией (видно на Windows-сервере по RDP). Перезапускаться ли сразу или ждать
/// человека (есть несохранённое), решает главная модель.
/// </summary>
/// <param name="updatedHere">Обновление поставило само это окно — про перезапуск оно спрашивает само, здесь молчим.</param>
/// <param name="newer">На диске версия новее запущенной (текст версии).</param>
public sealed class DiskUpdateWatcher(Func<bool> updatedHere, Action<string> newer)
{
    private readonly IUiTimer _timer = Ui.Timer(TimeSpan.FromSeconds(10));
    private bool _subscribed;

    // какой exe был при запуске (время и размер): пока он тот же, версию не читаем
    private (DateTime, long)? _exeStamp;

    /// <summary>Exe окна программы — рядом со сборкой.</summary>
    public static string MainExe => Path.Combine(AppContext.BaseDirectory, AppUpdater.WindowExe);

    /// <summary>
    /// Включить проверку по таймеру. Повторный вызов только запускает таймер: на Tick подписываемся один раз,
    /// иначе проверки шли бы по две за тик.
    /// </summary>
    public void Start()
    {
        if (!_subscribed)
        {
            _timer.Tick += (_, _) => Check();
            _subscribed = true;
        }
        _timer.Start();
    }

    /// <summary>Перезапуститься в новую версию: окно закрывается и запускает exe заново.</summary>
    public void Restart()
    {
        _timer.Stop();
        Ui.Platform.RestartAfterClose(MainExe);
        if (Ui.Platform.IsMainWindowVisible) _timer.Start(); // закрытие отменили (есть вопрос «сохранить?»)
    }

    /// <summary>Одна проверка exe на диске.</summary>
    public void Check()
    {
        // не Environment.ProcessPath: обновление переименовывает работающий exe в .old, и Windows отдаёт уже его
        var exe = MainExe;
        if (updatedHere()) return;
        (DateTime, long) stamp;
        try
        {
            var file = new FileInfo(exe);
            if (!file.Exists) return;
            stamp = (file.LastWriteTimeUtc, file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        _exeStamp ??= stamp;
        if (stamp == _exeStamp) return;

        // версию читаем с копии: для пути загруженного exe Windows отдаёт ресурсы из памяти, то есть старую версию
        var copy = Path.Combine(Path.GetTempPath(), $"evistool-version-{Environment.ProcessId}.exe");
        string? onDisk;
        try
        {
            File.Copy(exe, copy, overwrite: true);
            onDisk = System.Diagnostics.FileVersionInfo.GetVersionInfo(copy).ProductVersion?.Split('+')[0];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // файл как раз дописывают
        finally
        {
            try { File.Delete(copy); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (!ModVersion.TryParse(onDisk, out var disk)
            || !ModVersion.TryParse(AgentProtocol.AppVersion, out var running)
            || disk.CompareTo(running) <= 0)
        {
            _exeStamp = stamp; // тот же или старее — запоминаем и молчим
            return;
        }

        newer(onDisk!);
    }
}

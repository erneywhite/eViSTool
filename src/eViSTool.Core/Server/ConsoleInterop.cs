using System.Runtime.InteropServices;
using System.Text;

namespace eViSTool.Core.Server;

/// <summary>
/// Общая консоль с сервером. Процесс без окна наследует консоль родителя; если она в UTF-8,
/// сервер (он на .NET) тоже пишет в UTF-8 — иначе кириллица из логов VS превращается в «????».
/// Через неё же Ctrl+C для мягкой остановки: событие получают все процессы консоли, себя мы исключаем.
/// Всё это только про Windows: на Linux консоли нет, методы ничего не делают (HasConsole — false), сервер пишет
/// в канал в UTF-8, а мягкая остановка там — SIGTERM (<see cref="SoftStop"/>).
/// </summary>
public static partial class ConsoleInterop
{
    /// <summary>Есть ли у текущего процесса консоль (у агента — да, у окна eViSTool — нет).</summary>
    public static bool HasConsole => OperatingSystem.IsWindows() && GetConsoleCP() != 0;

    /// <summary>Переключить свою консоль на UTF-8 — дочерний сервер её унаследует.</summary>
    public static bool TryUseUtf8()
    {
        if (!HasConsole) return false;
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Признак «игнорировать Ctrl+C» наследуется дочерними процессами. Если нас запустили с ним
    /// (так делают некоторые оболочки и тестовые среды), сервер тоже не услышит мягкую остановку.
    /// Снимаем признак перед запуском сервера — сервер унаследует обычную обработку.
    /// </summary>
    public static void EnableCtrlCForChildren()
    {
        if (HasConsole) SetConsoleCtrlHandler(IntPtr.Zero, false);
    }

    /// <summary>Ctrl+C всем процессам общей консоли, кроме нас самих.</summary>
    public static bool SendCtrlCToConsole()
    {
        if (!HasConsole) return false;
        SetConsoleCtrlHandler(IntPtr.Zero, true);   // сами игнорируем
        try
        {
            return GenerateConsoleCtrlEvent(0 /* CTRL_C_EVENT */, 0);
        }
        finally
        {
            // обработчик возвращаем не сразу: событие доставляется асинхронно
            Task.Delay(1000).ContinueWith(_ => SetConsoleCtrlHandler(IntPtr.Zero, false));
        }
    }

    /// <summary>
    /// Ctrl+C серверу, запущенному не нами (например, оставшемуся от ViSST): подключаемся к его консоли,
    /// шлём событие и отключаемся. Работает только из процесса без своей консоли (окно eViSTool).
    /// Сервер VS на Ctrl+C сохраняет мир и завершается — в отличие от kill.
    /// </summary>
    public static bool SendCtrlCToForeignProcess(int pid)
    {
        if (!OperatingSystem.IsWindows() || HasConsole) return false;
        if (!AttachConsole((uint)pid)) return false;
        try
        {
            SetConsoleCtrlHandler(IntPtr.Zero, true); // сами не реагируем
            var ok = GenerateConsoleCtrlEvent(0 /* CTRL_C_EVENT */, 0);
            Thread.Sleep(300); // событие доставляется асинхронно — не отключаемся раньше времени
            return ok;
        }
        finally
        {
            FreeConsole();
            SetConsoleCtrlHandler(IntPtr.Zero, false);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetConsoleCP();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);
}

/// <summary>
/// Мягкая остановка своего сервера: на Windows — Ctrl+C через общую консоль (работает, когда сервер запущен в консоли
/// агента), на Linux — SIGTERM серверу по PID.
/// </summary>
public sealed class SharedConsoleCtrlC : ICtrlCSender
{
    public bool SendCtrlC(int pid) => SoftStop.Child(pid);
}

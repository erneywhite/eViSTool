using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace eViSTool.Desktop.Platform;

/// <summary>
/// Включены ли анимации в системе (docs/design.md, раздел 3): Windows — флажок «Показывать анимацию в Windows»
/// (SPI_GETCLIENTAREAANIMATION), Linux — ключ GNOME org.gnome.desktop.interface enable-animations, через gsettings.
/// На других системах и когда узнать не вышло (gsettings нет, схемы GNOME нет, вызов не ответил) — включены.
/// Читается один раз при запуске (App): выключены — к стилям подключается Themes/ForestStill.axaml.
/// </summary>
internal static class SystemMotion
{
    /// <summary>Откуда берётся настройка: из системы или, в тестах, из подмены.</summary>
    internal interface ISource
    {
        bool IsWindows { get; }
        bool IsLinux { get; }

        /// <summary>Флажок «Показывать анимацию в Windows»; null — не прочитался.</summary>
        bool? WindowsAnimation();

        /// <summary>Что gsettings напечатал про enable-animations («true» или «false»); null — gsettings нет или он не ответил.</summary>
        string? GnomeAnimation();
    }

    public static bool Read() => Read(new SystemSource());

    internal static bool Read(ISource source)
    {
        try
        {
            if (source.IsWindows) return source.WindowsAnimation() ?? true;
            if (source.IsLinux) return source.GnomeAnimation()?.Trim() != "false";
        }
        catch (Exception)
        {
            // настройка — только про вид: что бы ни случилось, окно открывается, анимации включены
        }
        return true;
    }

    private sealed class SystemSource : ISource
    {
        private const uint SpiGetClientAreaAnimation = 0x1042;

        public bool IsWindows => OperatingSystem.IsWindows();
        public bool IsLinux => OperatingSystem.IsLinux();

        public bool? WindowsAnimation()
        {
            var on = 0;
            return SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref on, 0) ? on != 0 : null;
        }

        public string? GnomeAnimation()
        {
            var psi = new ProcessStartInfo("gsettings")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true, // «нет такой схемы» вне GNOME — не в наш вывод
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "get", "org.gnome.desktop.interface", "enable-animations" }) psi.ArgumentList.Add(arg);
            try
            {
                using var process = Process.Start(psi);
                if (process is null) return null;
                var output = process.StandardOutput.ReadToEndAsync();
                _ = process.StandardError.ReadToEndAsync(); // читать и ошибки: иначе полный канал не даст ему выйти
                // окно ждёт ответа при запуске: зависший gsettings не должен его задерживать
                if (!process.WaitForExit(2000))
                {
                    try { process.Kill(); }
                    catch (InvalidOperationException) { } // успел выйти сам
                    return null;
                }
                return process.ExitCode == 0 && output.Wait(1000) ? output.Result : null;
            }
            catch (Win32Exception)
            {
                return null; // gsettings нет
            }
        }

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
    }
}

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// Настоящие логи сервера G-Portal (зонд 2026-10-17; адрес игрока, номер сервера и uid заменены). Строки берутся без
/// переводов строки: git на Windows может отдать файл с CRLF, а тесты сами решают, какие байты класть на «сервер».
/// </summary>
internal static class HostedLogSamples
{
    /// <summary>Запуск целиком: старт 13:27:08, игрок входит и выходит трижды, остановка из панели (SIGTERM).</summary>
    public static string[] Stopped => File.ReadAllLines(HostedFixture.Sample(Path.Combine("Hosting", "gportal-server-main.log")));

    /// <summary>Тот же лог, каким он был при запущенном сервере: обрывается на входе игрока в 13:31:18.</summary>
    public static string[] Running => Stopped[..(Array.FindIndex(Stopped, l => l.StartsWith("17.10.2026 13:31:18 [Event] Erney", StringComparison.Ordinal)) + 1)];

    /// <summary>Только начало запуска — до строки готовности.</summary>
    public static string[] Starting => Stopped[..Array.FindIndex(Stopped, l => l.Contains("now running on Port", StringComparison.Ordinal))];

    /// <summary>Тот же запуск до SIGTERM и сохранения мира, но без «Stopped the server!».</summary>
    public static string[] Stopping => Stopped[..Array.FindIndex(Stopped, l => l.Contains("Saved 1200 generating chunks", StringComparison.Ordinal))];

    /// <summary>Прошлый запуск из Logs/Archive/2026-10-17_13_25_13 (игрок не из белого списка, остановка из панели).</summary>
    public static string[] Archived => File.ReadAllLines(HostedFixture.Sample(Path.Combine("Hosting", "gportal-server-main-archived.log")));

    /// <summary>Строки → байты лога: UTF-8, каждая с «\n», как пишет сервер на Linux.</summary>
    public static byte[] Bytes(IEnumerable<string> lines) =>
        new System.Text.UTF8Encoding(false).GetBytes(string.Concat(lines.Select(l => l + "\n")));
}

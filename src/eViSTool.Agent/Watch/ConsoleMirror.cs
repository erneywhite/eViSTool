using eViSTool.Core.Server;

/// <summary>
/// Без окна (служба systemd, терминал) консоль сервера идёт ещё и в вывод агента — её видно в journalctl.
/// </summary>
internal static class ConsoleMirror
{
    public static void Write(ConsoleLine line)
    {
        try
        {
            if (line.Kind == ConsoleLineKind.Error) Console.Error.WriteLine(line.Text);
            else Console.Out.WriteLine(line.Kind == ConsoleLineKind.Input ? "> " + line.Text : line.Text);
        }
        catch (IOException) { } // вывод закрыт (агента отцепили от терминала) — консоль у окна от этого не страдает
    }
}

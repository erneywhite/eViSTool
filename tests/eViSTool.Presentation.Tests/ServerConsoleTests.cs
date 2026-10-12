using eViSTool.Core.Server;
using eViSTool.Presentation;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>Консоль раздела «Сервер»: «Копировать выделенные строки» из меню списка.</summary>
public sealed class ServerConsoleTests
{
    /// <summary>Платформа окна, которая запоминает, что положили в буфер обмена: настоящий буфер тест не трогает.</summary>
    private sealed class ClipboardPlatform : TestUiPlatform, IUiPlatform
    {
        public List<string> Copied { get; } = [];
        void IUiPlatform.SetClipboard(string text) => Copied.Add(text);
    }

    /// <summary>Раздел без главной модели: консоли она не нужна, а настоящая читала бы настройки пользователя.</summary>
    private static void WithServer(Action<ServerViewModel, ClipboardPlatform> body) => Sta.Run(() =>
    {
        var before = Ui.Platform;
        var clipboard = new ClipboardPlatform();
        Ui.Platform = clipboard;
        try { body(new ServerViewModel(null!), clipboard); }
        finally { Ui.Platform = before; }
    });

    private static void Fill(ServerViewModel server, params string[] texts)
    {
        for (var i = 0; i < texts.Length; i++)
            server.Lines.Add(new ConsoleLineViewModel(new ConsoleLine(i + 1, DateTime.Now, ConsoleLineKind.Output, texts[i])));
    }

    // Ctrl и Shift выделяют вразнобой (список отдаёт строки в порядке щелчков) — в буфере порядок консоли
    [Fact]
    public void CopyLines_KeepsConsoleOrder() => WithServer((server, clipboard) =>
    {
        Fill(server, "first", "second", "third", "fourth");
        var selected = new List<object> { server.Lines[3], server.Lines[0], server.Lines[2] };

        server.CopyLinesCommand.Execute(selected);

        Assert.Equal(["first" + Environment.NewLine + "third" + Environment.NewLine + "fourth"], clipboard.Copied);
    });

    // отправленная команда копируется так, как видна в консоли: с «> » впереди
    [Fact]
    public void CopyLines_TakesShownText() => WithServer((server, clipboard) =>
    {
        server.Lines.Add(new ConsoleLineViewModel(new ConsoleLine(1, DateTime.Now, ConsoleLineKind.Input, "/time")));

        server.CopyLinesCommand.Execute(new List<object> { server.Lines[0] });

        Assert.Equal(["> /time"], clipboard.Copied);
    });

    // ничего не выделено (или строки уже убрали из консоли) — буфер не трогаем
    [Fact]
    public void CopyLines_NothingSelected_LeavesClipboard() => WithServer((server, clipboard) =>
    {
        Fill(server, "only");
        var gone = new ConsoleLineViewModel(new ConsoleLine(9, DateTime.Now, ConsoleLineKind.Output, "cleared"));

        server.CopyLinesCommand.Execute(null);
        server.CopyLinesCommand.Execute(new List<object>());
        server.CopyLinesCommand.Execute(new List<object> { gone });

        Assert.Empty(clipboard.Copied);
    });
}

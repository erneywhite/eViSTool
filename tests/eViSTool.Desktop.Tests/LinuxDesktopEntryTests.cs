using eViSTool.Desktop.Platform;

namespace eViSTool.Desktop.Tests;

public sealed class LinuxDesktopEntryTests
{
    [Fact]
    public void Quote_EscapesForDesktopEntry()
    {
        Assert.Equal("\"/home/user/eViSTool/eViSTool\"", LinuxDesktopEntry.Quote("/home/user/eViSTool/eViSTool"));
        Assert.Equal("\"/home/a b/eViSTool\"", LinuxDesktopEntry.Quote("/home/a b/eViSTool"));
        // $ внутри кавычек экранируется для разбора Exec, а обратный слэш ещё раз — как в любой строке ярлыка
        Assert.Equal("\"/opt/\\\\$x/100%%/eViSTool\"", LinuxDesktopEntry.Quote("/opt/$x/100%/eViSTool"));
    }

    [Fact]
    public void Content_PointsToProgramAndIcon()
    {
        var text = LinuxDesktopEntry.Content("/opt/evistool/eViSTool", "/home/u/.local/share/icons/evistool.png");
        Assert.Contains("\nExec=\"/opt/evistool/eViSTool\"\n", text);
        Assert.Contains("\nIcon=/home/u/.local/share/icons/evistool.png\n", text);
        Assert.StartsWith("[Desktop Entry]\n", text);
        Assert.DoesNotContain('\r', text);
    }
}

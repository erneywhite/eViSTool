using eViSTool.Core.Game;

namespace eViSTool.Core.Tests;

public sealed class GameShortcutTests
{
    [Fact]
    public void GameDir_IsTakenFromThePathLine_OfTheDesktopEntry()
    {
        // так пишет install.sh из архива игры (1.22.7)
        const string shortcut = "#!/usr/bin/xdg-open\n[Desktop Entry]\nName=Vintage Story 1.22.7\nExec=bash run.sh\nTerminal=false\n"
            + "Type=Application\nCategories=Game;\nPath=/home/user/.local/share/vintagestory\nIcon=/home/user/.local/share/vintagestory/assets/gameicon.png\n";
        Assert.Equal("/home/user/.local/share/vintagestory", GameInstall.GameDirFromShortcut(shortcut));
        Assert.Equal("/opt/vs", GameInstall.GameDirFromShortcut("[Desktop Entry]\r\nPath=/opt/vs\r\n"));
    }

    [Fact]
    public void NoPath_OrPathOutsideTheEntry_GivesNothing()
    {
        Assert.Null(GameInstall.GameDirFromShortcut("[Desktop Entry]\nExec=bash run.sh\n"));
        Assert.Null(GameInstall.GameDirFromShortcut("[Desktop Action x]\nPath=/elsewhere\n"));
        Assert.Null(GameInstall.GameDirFromShortcut("[Desktop Entry]\nPath=\n"));
    }
}

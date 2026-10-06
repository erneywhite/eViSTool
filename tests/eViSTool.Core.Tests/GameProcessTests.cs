using System.Diagnostics;
using eViSTool.Core.Game;

namespace eViSTool.Core.Tests;

/// <summary>Копии игры с разными папками данных различаются по --dataPath в командной строке процесса.</summary>
public sealed class GameProcessTests
{
    [Theory]
    [InlineData("\"S:\\Games\\Vintagestory\\Vintagestory.exe\" --dataPath \"C:\\Data\\Мой профиль\"", "C:\\Data\\Мой профиль")]
    [InlineData("Vintagestory.exe --dataPath C:\\Data\\solo --tracelog", "C:\\Data\\solo")]
    [InlineData("Vintagestory.exe --dataPath=C:\\Data\\eq", "C:\\Data\\eq")]
    [InlineData("Vintagestory.exe --DATAPATH \"C:\\x\"", "C:\\x")]
    [InlineData("Vintagestory.exe", null)]
    public void DataPath_IsReadFromTheCommandLine(string commandLine, string? expected) =>
        Assert.Equal(expected, ProcessCommandLine.Argument(commandLine, "--dataPath"));

    [Fact]
    public void SameDataPath_TreatsTheDefaultFolderAsNoArgument_AndIgnoresCaseAndSlash()
    {
        Assert.True(GameProcess.SameDataPath(null, null));
        Assert.True(GameProcess.SameDataPath(GameInstall.DefaultDataDir, null)); // явно указана стандартная — то же самое
        Assert.True(GameProcess.SameDataPath(@"C:\Data\Solo\", @"c:\data\solo"));
        Assert.False(GameProcess.SameDataPath(@"C:\Data\Solo", null));
        Assert.False(GameProcess.SameDataPath(@"C:\Data\Solo", @"C:\Data\SoloTwo"));
    }

    [Fact]
    public void CommandLine_OfARealProcess_IsRead()
    {
        // настоящий процесс с «папкой данных» в командной строке
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 6 127.0.0.1 >nul & rem --dataPath \"C:\\Some Data\"")
        {
            CreateNoWindow = true, UseShellExecute = false,
        })!;
        try
        {
            var cmd = ProcessCommandLine.Get(p.Id);
            Assert.NotNull(cmd);
            Assert.Contains("--dataPath", cmd);
            Assert.Equal("C:\\Some Data", ProcessCommandLine.Argument(cmd!, "--dataPath"));
        }
        finally
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void CommandLine_OfAMissingProcess_IsNull() => Assert.Null(ProcessCommandLine.Get(int.MaxValue - 7));
}

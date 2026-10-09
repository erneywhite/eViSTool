using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

/// <summary>Где агент без окна находит сервер: ключи, своя папка, соседние, server.sh, стандартное место.</summary>
public sealed class ServerLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "evistool-locator-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    /// <summary>Папка «игры»: VintagestoryServer.dll, а на Windows — и exe.</summary>
    private string Game(string name)
    {
        var dir = Dir(name);
        File.WriteAllText(Path.Combine(dir, "VintagestoryServer.dll"), "");
        if (OperatingSystem.IsWindows()) File.WriteAllText(Path.Combine(dir, "VintagestoryServer.exe"), "");
        return dir;
    }

    private static string Script(string dir, string? game, string? data)
    {
        var path = Path.Combine(dir, ServerLocator.ScriptName);
        File.WriteAllText(path, $$"""
            #!/bin/bash
            #Settings
            USERNAME='vintagestory'
            {{(game is null ? "" : $"VSPATH='{game}'")}}
            {{(data is null ? "" : $"DATAPATH='{data}'")}}
            vs_setup() {
                VSPATH="${2:-/home/${1}/server}"
            }
            """);
        return path;
    }

    [Fact]
    public void ParseScript_ReadsTheHeader_AsBashWould()
    {
        // как в официальном server.sh: одинарные кавычки; присваивание внутри функции — не настройка
        var official = ServerLocator.ParseScript("""
            #!/bin/bash
            HISTORY=1024
            USERNAME='vintagestory'
            VSPATH='/home/vintagestory/server'
            DATAPATH='/var/vintagestory/data'
            SERVICE="VintagestoryServer.dll"
            INVOCATION="dotnet ${SERVICE} --dataPath \"${DATAPATH}\" ${OPTIONS}"
            vs_setup() {
                VSPATH="${2:-/home/${1}/server}"
            }
            """);
        Assert.Equal(new ServerScript("/home/vintagestory/server", "/var/vintagestory/data"), official);

        // двойные кавычки с переменными из той же шапки, без кавычек — до комментария; CRLF не мешает
        Assert.Equal(new ServerScript("/srv/vs/server", "/srv/vs/data two"), ServerLocator.ParseScript(
            "BASE=/srv/vs   # корень\r\nexport VSPATH=\"${BASE}/server\"\r\nDATAPATH=\"$BASE/data two\"\r\nVSPATH='/второе/игнорируется'\r\n"));

        // склейка кусков и экранирование
        Assert.Equal(new ServerScript("/opt/my vs", "/opt/a$b"), ServerLocator.ParseScript(
            "VSPATH='/opt/'\"my vs\"\nDATAPATH=/opt/a\\$b\n"));
    }

    [Theory]
    [InlineData("DATAPATH=\"$HOME/data\"")]      // переменная не из шапки — чья она, не угадать
    [InlineData("DATAPATH=`pwd`/data")]          // команда
    [InlineData("DATAPATH=\"${1:-/data}\"")]     // подстановка с условием
    [InlineData("DATAPATH='/data")]              // кавычка не закрыта
    [InlineData("DATAPATH=data")]                // не полный путь
    [InlineData("# DATAPATH='/data'")]           // закомментировано
    public void ParseScript_WhatCannotBeComputed_IsNotGuessed(string line)
    {
        Assert.Null(ServerLocator.ParseScript("VSPATH='/vs'\n" + line + "\n").DataDir);
    }

    [Fact]
    public void ExplicitGame_TakesDataFromItsServerSh()
    {
        var game = Game("game");
        var data = Path.Combine(_root, "data");
        var script = Script(game, game, data);

        var found = ServerLocator.Locate(game, null, Dir("evistool"), standardDirs: []);
        Assert.Equal(new ServerLocation(game, data, script), found);

        // явный --data важнее server.sh
        var other = Dir("other-data");
        Assert.Equal(other, ServerLocator.Locate(game, other, Dir("evistool"), standardDirs: [])!.DataDir);
    }

    [Fact]
    public void ExplicitGame_ThatIsNotAServer_IsNotReplacedByAnotherOne()
    {
        Game("evistool"); // агент лежит в папке игры, но сказали искать в другой
        var empty = Dir("empty");
        var tried = new List<string>();

        Assert.Null(ServerLocator.Locate(empty, null, Path.Combine(_root, "evistool"), tried, standardDirs: []));
        Assert.Equal([empty], tried);
    }

    [Fact]
    public void AgentInsideTheGameFolder_FindsIt_DataAsTheGameWouldChoose()
    {
        var game = Game("server");

        var found = ServerLocator.Locate(null, null, game, standardDirs: []);
        Assert.Equal(new ServerLocation(game, ServerLocator.DefaultDataDir, null), found);
        Assert.EndsWith("VintagestoryData", ServerLocator.DefaultDataDir);
        Assert.True(Path.IsPathRooted(ServerLocator.DefaultDataDir));
    }

    [Fact]
    public void NeighbourFolder_WithServerSh_LikeTheOfficialSetup()
    {
        // /home/vintagestory/{evistool, server}: игра — соседняя папка, данные — из её server.sh
        var own = Dir("evistool");
        var game = Game("server");
        var data = Path.Combine(_root, "var-data");
        var script = Script(game, game, data);
        Dir("aaa-not-a-server");

        Assert.Equal(new ServerLocation(game, data, script), ServerLocator.Locate(null, null, own, standardDirs: []));
    }

    [Fact]
    public void ServerSh_NextToTheAgent_PointsToTheGameElsewhere()
    {
        var own = Dir("evistool");
        var game = Game(Path.Combine("far", "away", "vs"));
        var data = Path.Combine(_root, "far", "data");
        var script = Script(own, game, data);

        Assert.Equal(new ServerLocation(game, data, script), ServerLocator.Locate(null, null, own, standardDirs: []));
    }

    [Fact]
    public void StandardPlace_IsTheLastResort()
    {
        var own = Dir(Path.Combine("opt", "evistool"));
        var standard = Game(Path.Combine("home", "vintagestory", "server"));
        var data = Path.Combine(_root, "var", "vintagestory", "data");
        var script = Script(standard, standard, data);

        Assert.Equal(new ServerLocation(standard, data, script), ServerLocator.Locate(null, null, own, standardDirs: [standard]));
        // server.sh лежит в папке игры, но без VSPATH — его DATAPATH всё равно про эту игру
        File.WriteAllText(script, $"DATAPATH='{data}'\n");
        Assert.Equal(data, ServerLocator.Locate(null, null, own, standardDirs: [standard])!.DataDir);
    }

    [Fact]
    public void NothingAnywhere_SaysWhereItLooked()
    {
        var own = Dir("evistool");
        Dir("neighbour");
        var standard = Path.Combine(_root, "no-such-server");
        var tried = new List<string>();

        Assert.Null(ServerLocator.Locate(null, null, own, tried, standardDirs: [standard]));
        Assert.Equal([own, Path.Combine(_root, "*"), standard], tried);
    }

    [Fact]
    public void GameFolder_NeedsTheServerDll_AndOnWindowsTheExe()
    {
        var dir = Dir("half");
        Assert.False(ServerLocator.IsGameDir(dir));
        File.WriteAllText(Path.Combine(dir, "VintagestoryServer.dll"), "");
        Assert.Equal(!OperatingSystem.IsWindows(), ServerLocator.IsGameDir(dir));
        File.WriteAllText(Path.Combine(dir, "VintagestoryServer.exe"), "");
        Assert.True(ServerLocator.IsGameDir(dir));
    }
}

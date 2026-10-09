using eViSTool.Core.Server;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests;

/// <summary>data/agent.json — настройки агента без окна: запись командой setup, правки руками, чтение при запуске.</summary>
public sealed class AgentConfigTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "evistool-agentcfg-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string File_ => Path.Combine(_root, "data", AgentConfig.FileName);

    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    [Fact]
    public void Save_NewFile_HasEveryField_ReadableByAPerson()
    {
        var (game, data) = (Dir("server"), Dir("vs-data"));
        AgentConfig.Save(File_, new AgentConfigUpdate(game, data));

        var text = File.ReadAllText(File_);
        Assert.Contains("\n  \"GameDir\": ", text.Replace("\r\n", "\n")); // с отступами, по полю в строке
        var doc = JObject.Parse(text);
        Assert.Equal(["GameDir", "DataDir", "Profile", "StartServer", "ServerName", "ServerArgs", "Language"],
            doc.Properties().Select(p => p.Name));
        Assert.Equal(("server", true), ((string?)doc["Profile"], (bool)doc["StartServer"]!));

        var config = AgentConfig.Load(File_)!;
        Assert.Equal((game, data, "server", true), (config.GameDir, config.DataDir, config.Profile, config.StartServer));
        Assert.Null(config.ServerName); // «» — не задано: имя возьмётся из serverconfig.json
        Assert.Null(config.Language);
        Assert.Empty(config.ServerArgs);
    }

    [Fact]
    public void SaveAgain_KeepsWhatWasEditedByHand()
    {
        AgentConfig.Save(File_, new AgentConfigUpdate(Dir("old-server"), Dir("old-data")));
        // человек поправил файл: своё имя и профиль, не запускать сервер, заметка, поле с другим регистром
        var doc = JObject.Parse(File.ReadAllText(File_));
        doc["ServerName"] = "Survival Island";
        doc["Profile"] = "survival";
        doc["StartServer"] = false;
        doc["ServerArgs"] = new JArray("--withconfig", "{ }");
        doc.Property("GameDir")!.Replace(new JProperty("gameDir", "/somewhere/else"));
        doc.Add("Note", "моя заметка");
        File.WriteAllText(File_, doc.ToString());

        var (game, data) = (Dir("server"), Dir("data"));
        AgentConfig.Save(File_, new AgentConfigUpdate(game, data));

        var after = JObject.Parse(File.ReadAllText(File_));
        Assert.Equal(["gameDir", "DataDir", "Profile", "StartServer", "ServerName", "ServerArgs", "Language", "Note"],
            after.Properties().Select(p => p.Name)); // поле не задвоилось, порядок прежний
        Assert.Equal("моя заметка", (string?)after["Note"]);
        var config = AgentConfig.Load(File_)!;
        Assert.Equal((game, data), (config.GameDir, config.DataDir)); // папки — новые, остальное — как поправили
        Assert.Equal(("survival", false, "Survival Island"), (config.Profile, config.StartServer, config.ServerName));
        Assert.Equal(["--withconfig", "{ }"], config.ServerArgs);

        // заданное ключами setup — поверх
        AgentConfig.Save(File_, new AgentConfigUpdate(game, data, Profile: "pve", StartServer: true, ServerName: "PvE", ServerArgs: ["--x"]));
        config = AgentConfig.Load(File_)!;
        Assert.Equal(("pve", true, "PvE"), (config.Profile, config.StartServer, config.ServerName));
        Assert.Equal(["--x"], config.ServerArgs);
        Assert.Equal("моя заметка", (string?)JObject.Parse(File.ReadAllText(File_))["Note"]);
    }

    [Theory]
    [InlineData("{ \"GameDir\": ", true)]               // оборван
    [InlineData("[ ]", true)]                           // не объект
    [InlineData("{ \"StartServer\": \"yes\" }", false)] // не то значение (setup с таким файлом не идёт — см. AgentSetupTests)
    [InlineData("{ \"ServerArgs\": 5 }", false)]
    [InlineData("{ \"Profile\": \"a/b\" }", false)]     // профиль — часть имён файлов
    public void BrokenFile_IsAnError_SayingWhere_AndIsNotOverwritten(string text, bool notJson)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, text);

        var error = Assert.Throws<InvalidDataException>(() => AgentConfig.Load(File_));
        Assert.Contains(File_, error.Message);
        if (!notJson) return;
        Assert.Throws<InvalidDataException>(() => AgentConfig.Save(File_, new AgentConfigUpdate(Dir("g"), Dir("d"))));
        Assert.Equal(text, File.ReadAllText(File_));
    }

    [Fact]
    public void Load_EmptyValuesAreUnset_RelativePathsFromTheFileFolder_NamesInAnyCase()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, """
            { "gamedir": "../server", "DataDir": "", "Profile": " ", "ServerName": "  ", "ServerArgs": null, "language": "ru" }
            """);
        var config = AgentConfig.Load(File_)!;
        Assert.Equal(Path.Combine(_root, "server"), config.GameDir);
        Assert.Null(config.DataDir);
        Assert.Null(config.Profile);
        Assert.Null(config.ServerName);
        Assert.Empty(config.ServerArgs);
        Assert.Equal("ru", config.Language);
        Assert.True(config.StartServer); // не указано — по умолчанию сервер запускается

        // пустой файл — всё по умолчанию, файла нет — null
        File.WriteAllText(File_, "");
        Assert.True(AgentConfig.Load(File_)!.StartServer);
        Assert.Null(AgentConfig.Load(Path.Combine(_root, "nope.json")));
    }

    [Fact]
    public void LivesNextToTheAgentsFolder_FoundOnlyIfItIsThere()
    {
        var agents = Path.Combine(_root, "data", "agents");
        Assert.Equal(File_, AgentConfig.FileFor(agents));
        Assert.Equal(File_, AgentConfig.FileFor(agents + Path.DirectorySeparatorChar));
        Assert.Null(AgentConfig.Find(agents));
        Assert.False(Directory.Exists(Path.Combine(_root, "data"))); // поиск ничего не создаёт

        AgentConfig.Save(File_, new AgentConfigUpdate());
        Assert.Equal(File_, AgentConfig.Find(agents));
    }

    [Fact]
    public void BackupName_FromAgentJson_ThenServerConfig_ThenWorld()
    {
        var data = Dir("data");
        Assert.Equal("world", AgentConfig.BackupNameFor(null, data)); // нет ни того, ни другого
        Assert.Null(AgentConfig.DisplayNameFor(null, data));

        File.WriteAllText(Path.Combine(data, "serverconfig.json"), """{ "ServerName": "Survival Island: PvE", "Port": 42420 }""");
        Assert.Equal("Survival_Island__PvE", AgentConfig.BackupNameFor(null, data)); // /genbackup не любит пробелы, файл — двоеточия
        Assert.Equal("Survival Island: PvE", AgentConfig.DisplayNameFor(new AgentConfig(), data));

        var named = new AgentConfig { ServerName = "Мой сервер" };
        Assert.Equal("Мой_сервер", AgentConfig.BackupNameFor(named, data)); // agent.json главнее
        Assert.Equal("Мой сервер", AgentConfig.DisplayNameFor(named, data));

        File.WriteAllText(Path.Combine(data, "serverconfig.json"), "{ broken");
        Assert.Equal("world", AgentConfig.BackupNameFor(null, data));
    }

    [Fact]
    public void Pick_KeyFirst_ThenFile()
    {
        Assert.Equal("key", AgentConfig.Pick("key", "file"));
        Assert.Equal("file", AgentConfig.Pick(" ", "file"));
        Assert.Equal("file", AgentConfig.Pick(null, " file "));
        Assert.Null(AgentConfig.Pick(null, ""));
    }
}

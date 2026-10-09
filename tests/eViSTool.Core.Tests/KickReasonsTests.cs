using eViSTool.Core.Diagnostics;

namespace eViSTool.Core.Tests;

/// <summary>Тексты «выгнали / забанили» — из переводов игры на языке, выбранном в её настройках.</summary>
public sealed class KickReasonsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-kicks-" + Guid.NewGuid().ToString("N"));
    private string Game => Path.Combine(_root, "game");
    private string Data => Path.Combine(_root, "data");

    public KickReasonsTests()
    {
        var lang = Directory.CreateDirectory(Path.Combine(Game, "assets", "game", "lang")).FullName;
        Directory.CreateDirectory(Data);
        File.WriteAllText(Path.Combine(lang, "en.json"), """
            {
            	"You've been kicked by {0}": "You've been kicked by {0}",
            	"You've been kicked by {0}, reason: {1}": "You've been kicked by {0}, reason: {1}",
            	"cmdban-youvebeenbanned": "You've been banned by {0}{1}",
            }
            """);
        // формат как у игры: табы, запятая в конце, комментарий
        File.WriteAllText(Path.Combine(lang, "de.json"), """
            {
            	// Spieler
            	"You've been kicked by {0}": "Du wurdest von {0} rausgeworfen",
            	"cmdban-youvebeenbanned": "Du wurdest von {0} gebannt{1}",
            }
            """);
        File.WriteAllText(Path.Combine(Data, "clientsettings.json"), """{ "stringSettings": { "language": "de" } }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void GameLanguage_AndEnglish_AreRecognized()
    {
        var kicks = KickReasons.For(Game, Data);

        Assert.True(kicks.Matches("Du wurdest von Console Admin rausgeworfen"));
        Assert.True(kicks.Matches("Du wurdest von Birchwood gebannt bis 10.10.2026"));
        Assert.True(kicks.Matches("You've been kicked by Console Admin, reason: afk"));
        Assert.False(kicks.Matches("Too many errors"));
        Assert.False(kicks.Matches("Server shutting down"));
    }

    [Fact]
    public void NoGameFiles_FallsBackToBuiltIn()
    {
        var kicks = KickReasons.For(Path.Combine(_root, "nowhere"), Data);

        Assert.True(kicks.Matches("Вас выгнал Console Admin"));
        Assert.True(kicks.Matches("You've been kicked by Console Admin"));
    }
}

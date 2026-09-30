using eViSTool.Core.Localization;
using eViSTool.Core.Server.Config;
using Newtonsoft.Json;

namespace eViSTool.Core.Tests;

/// <summary>
/// Ключи «cfg.*» и «ws.*» собираются в коде на ходу — общий тест «каждый использованный ключ есть в словаре» их не видит.
/// Здесь они проверяются по схеме полей и каталогу настроек мира, для каждого языка.
/// </summary>
public sealed class ServerConfigTextsTests
{
    public static TheoryData<string> Languages => new(Loc.Available.Select(a => a.Code));

    private static void AssertTranslated(string language, IEnumerable<string> keys)
    {
        var missing = keys.Except(Loc.Keys(language)).ToList();
        Assert.True(missing.Count == 0, $"{language}: нет в словаре: {string.Join(", ", missing)}");
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryKnownField_HasLabelAndHint(string language)
    {
        AssertTranslated(language, ServerConfigSchema.Known.Select(f => f.LabelKey));
        AssertTranslated(language, ServerConfigSchema.Known.Select(f => f.HintKey));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryGroup_HasTitle(string language)
    {
        var groups = ServerConfigSchema.Known.Select(f => f.GroupKey).Distinct().ToList();

        Assert.Equal(16, groups.Count); // «network» есть и на «Основном», и в «Дополнительно» — заголовок один
        Assert.Contains("cfg.group." + ServerConfigSchema.OtherGroup, groups); // туда же попадают поля вне схемы
        AssertTranslated(language, groups);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryChoice_HasLabel(string language)
    {
        var choices = ServerConfigSchema.Known.SelectMany(f => f.Choices).ToList();

        Assert.Equal(33 + 3 + 3 + 5 + 2, choices.Count); // языки, белый список, защита от читов, стили игры, типы мира
        Assert.All(ServerConfigSchema.Known, f =>
            Assert.All(f.Choices, c => Assert.Equal($"cfg.{f.Path}.choice.{c.Value}", c.LabelKey)));
        AssertTranslated(language, choices.Select(c => c.LabelKey));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryWorldSetting_HasLabelHintCategoryAndValueLabels(string language)
    {
        var all = WorldSettingCatalog.All;

        AssertTranslated(language, all.Select(s => s.LabelKey));
        AssertTranslated(language, all.Select(s => s.HintKey));
        AssertTranslated(language, all.Select(s => s.CategoryKey));
        AssertTranslated(language, WorldSettingCatalog.Categories.Select(c => "ws.category." + c));
        AssertTranslated(language, all.Where(s => s.Type == WorldSettingType.Choice).SelectMany(s => s.Values.Select(s.ValueLabelKey)));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Texts_AreNotEmpty_AndHintsAreShort(string language)
    {
        var texts = Texts(language).Where(t => t.Key.StartsWith("cfg.") || t.Key.StartsWith("ws.")).ToList();

        Assert.True(texts.Count > 700);
        Assert.All(texts, t => Assert.False(string.IsNullOrWhiteSpace(t.Value), t.Key));
        Assert.All(texts, t => Assert.Equal(t.Value.Trim(), t.Value));
        // подсказка — строка-две под полем, а не справка
        Assert.All(texts.Where(t => t.Key.EndsWith(".hint")), t => Assert.True(t.Value.Length <= 170, $"{t.Key}: {t.Value.Length}"));
    }

    [Fact]
    public void Texts_SayWhatTheGameMeans()
    {
        var ru = Texts("ru");
        var en = Texts("en");

        Assert.Equal("Порт", ru["cfg.Port"]);
        Assert.Equal("Port", en["cfg.Port"]);
        Assert.Equal("Сеть", ru["cfg.group.network"]);
        Assert.Equal("Генерация мира", ru["ws.category.worldgen"]);

        // 1 — это «выключен», а не «включён»
        Assert.Equal(("Выключен", "Включён"), (ru["cfg.WhitelistMode.choice.1"], ru["cfg.WhitelistMode.choice.2"]));
        Assert.Equal(("Off", "On"), (en["cfg.WhitelistMode.choice.1"], en["cfg.WhitelistMode.choice.2"]));

        // языки — самоназвания, одни на оба словаря
        Assert.Equal("Русский (ru)", en["cfg.ServerLanguage.choice.ru"]);
        var languages = ServerConfigSchema.Known.Single(f => f.Path == "ServerLanguage").Choices;
        Assert.All(languages, c => Assert.Equal(en[c.LabelKey], ru[c.LabelKey]));
        Assert.All(languages, c => Assert.EndsWith($" ({c.Value})", en[c.LabelKey]));

        // про ошибку игры с порогом свободного места сказано прямо в подсказке
        Assert.Contains("2048", ru["cfg.DieBelowDiskSpaceMb.hint"]);
        Assert.Contains("2048", en["cfg.DieBelowDiskSpaceMb.hint"]);
    }

    /// <summary>Словарь языка как он вшит в сборку — без переключения языка программы (он общий для всех тестов).</summary>
    private static Dictionary<string, string> Texts(string language)
    {
        var assembly = typeof(Loc).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith($".Lang.{language}.json", StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        return JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd())!;
    }
}

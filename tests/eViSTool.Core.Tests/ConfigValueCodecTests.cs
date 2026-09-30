using System.Globalization;
using eViSTool.Core.Localization;
using eViSTool.Core.Server.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests;

public sealed class ConfigValueCodecTests : IDisposable
{
    private readonly ConfigSamples _samples = new();

    public void Dispose() => _samples.Dispose();

    private static ConfigFieldSpec Spec(ConfigValueKind kind) =>
        new() { Path = "Field", Section = ConfigSections.Advanced, Group = "other", Kind = kind };

    private static ConfigFieldSpec Known(string path) => ServerConfigSchema.Known.Single(f => f.Path == path);

    private static JToken Parse(string? text, ConfigFieldSpec spec)
    {
        Assert.True(ConfigValueCodec.TryParse(text, spec, out var token, out var errorKey, out var errorArgs), $"{spec.Path}: {errorKey}");
        Assert.Null(errorKey);
        Assert.Empty(errorArgs);
        return Assert.IsAssignableFrom<JToken>(token);
    }

    private static object?[] Fails(string? text, ConfigFieldSpec spec, string expectedKey)
    {
        Assert.False(ConfigValueCodec.TryParse(text, spec, out var token, out var errorKey, out var errorArgs));
        Assert.Null(token);
        Assert.Equal(expectedKey, errorKey);
        return errorArgs;
    }

    private static void AssertNull(string? text, ConfigFieldSpec spec) => Assert.Equal(JTokenType.Null, Parse(text, spec).Type);

    // ---- образцы целиком

    [Theory]
    [InlineData(ConfigSamples.Real)]
    [InlineData(ConfigSamples.Default)]
    public void EveryFieldOfSamples_SurvivesTextRoundTrip(string sample)
    {
        var doc = _samples.Open(sample);
        var fields = ServerConfigSchema.FieldsFor(doc);
        Assert.True(fields.Count > 60);

        foreach (var spec in fields)
        {
            var token = doc.Get(spec.Path)!;
            var text = ConfigValueCodec.ToText(token, spec);

            if (spec.Kind == ConfigValueKind.ReadOnly)
            {
                Fails(text, spec, "cfgerr.readOnly");
                Assert.True(ConfigValueCodec.TryParse("anything", spec, token, out var same, out _, out _));
                Assert.True(JToken.DeepEquals(token, same), spec.Path);
                continue;
            }

            // так поле и сохраняется: текст не тронут — токен тот же, документ не меняется
            Assert.True(ConfigValueCodec.TryParse(text, spec, token, out var kept, out var errorKey, out _), $"{spec.Path}: {errorKey}");
            Assert.True(JToken.DeepEquals(token, kept), spec.Path);
            doc.Set(spec.Path, kept);

            // единственное значение образцов, которое схема уже не принимает: 4096 МБ в живом конфиге (см. тесты схемы)
            if (spec.Path == "DieBelowDiskSpaceMb" && sample == ConfigSamples.Real)
            {
                Fails(text, spec, "cfgerr.range");
                continue;
            }

            // и без исходного значения текст разбирается в тот же токен
            var parsed = Parse(text, spec);
            Assert.True(JToken.DeepEquals(token, parsed), $"{spec.Path}: {token.ToString(Formatting.None)} → «{text}» → {parsed.ToString(Formatting.None)}");
            doc.Set(spec.Path, parsed);
        }

        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void ToText_ShowsValuesAsWritten()
    {
        var doc = _samples.Open();
        string Text(string path) => ConfigValueCodec.ToText(doc.Get(path), ServerConfigSchema.FieldsFor(doc).Single(f => f.Path == path));

        Assert.Equal("42420", Text("Port"));
        Assert.Equal("33.333332", Text("TickTime"));
        Assert.Equal("14.0", Text("AntiAbuseBlockBurstAbuseBanDays"));
        Assert.Equal("false", Text("Upnp"));
        Assert.Equal("1", Text("WhitelistMode"));
        Assert.Equal("", Text("StartupCommands")); // null
        Assert.Equal("", Text("MasterserverUrl")); // null
        Assert.Equal("null", Text("DefaultSpawn")); // null в поле сырого JSON
        Assert.Equal("16", Text("NextPlayerGroupUid")); // только для чтения — тоже как записано
        Assert.Equal("false", Text("RepairMode"));
        Assert.Equal("Mods\nC:\\Users\\Admin\\AppData\\Roaming\\VintagestoryData\\Mods", Text("ModPaths"));

        Assert.Equal("", ConfigValueCodec.ToText(null, Spec(ConfigValueKind.Integer)));
        Assert.Equal("{\n  \"a\": [\n    1,\n    0.50\n  ]\n}", ConfigValueCodec.ToText(new JObject { ["a"] = new JArray(1, 0.50m) }, Spec(ConfigValueKind.Json)));
        Assert.Equal("\"text\"", ConfigValueCodec.ToText("text", Spec(ConfigValueKind.Json)));
    }

    // ---- числа

    [Fact]
    public void Integer_ParsesWholeNumbersWithinBounds()
    {
        var port = Known("Port");
        Assert.Equal(JTokenType.Integer, Parse("8080", port).Type);
        Assert.Equal(8080, (int)Parse(" 8080 ", port));
        Assert.Equal(-5, (int)Parse("-5", Spec(ConfigValueKind.Integer)));

        Fails("abc", port, "cfgerr.notInteger");
        Fails("80.5", port, "cfgerr.notInteger");
        Fails("1 000", port, "cfgerr.notInteger");
        Fails("", port, "cfgerr.required");
        Fails(null, port, "cfgerr.required");

        Assert.Equal([1m, 65535m], Fails("0", port, "cfgerr.range"));
        Assert.Equal([1m, 65535m], Fails("65536", port, "cfgerr.range"));
        Assert.Equal([1m], Fails("0", Known("MaxClients"), "cfgerr.min"));
        Assert.Equal([10m], Fails("11", Spec(ConfigValueKind.Integer) with { Max = 10 }, "cfgerr.max"));
    }

    [Fact]
    public void Integer_WithoutBounds_StaysWithinInt32()
    {
        var spec = Spec(ConfigValueKind.Integer);

        Assert.Equal(int.MaxValue, (long)Parse("2147483647", spec));
        Assert.Equal(int.MinValue, (long)Parse("-2147483648", spec));
        Assert.Equal([(decimal)int.MaxValue], Fails("2147483648", spec, "cfgerr.max"));
        Assert.Equal([(decimal)int.MinValue], Fails("-2147483649", spec, "cfgerr.min"));
        Assert.Equal([(decimal)int.MaxValue], Fails("3000000000", Known("MaxClients"), "cfgerr.max"));

        AssertNull("", Known("WorldConfig.MapSizeY"));
        AssertNull("  ", Known("WorldConfig.MapSizeY"));
        Assert.Equal(320, (int)Parse("320", Known("WorldConfig.MapSizeY")));
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("")]
    public void Decimal_AcceptsDotAndComma_InAnyCulture(string culture)
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var spec = Spec(ConfigValueKind.Decimal);

            Assert.Equal(0.5m, (decimal)Parse("0.5", spec));
            Assert.Equal(0.5m, (decimal)Parse("0,5", spec));
            Assert.Equal(-1.25m, (decimal)Parse(" -1.25 ", spec));
            Assert.Equal("33.333332", Parse("33.333332", spec).ToString(Formatting.None));
            Assert.Equal("33.333332", ConfigValueCodec.ToText(Parse("33,333332", spec), spec));

            // целое в дробном поле пишется дробным — тип поля в файле не меняется
            var whole = Parse("5", spec);
            Assert.Equal(JTokenType.Float, whole.Type);
            Assert.Equal("5.0", whole.ToString(Formatting.None));

            Fails("abc", spec, "cfgerr.notNumber");
            Fails("1.2.3", spec, "cfgerr.notNumber");
            Fails("", spec, "cfgerr.required");
            AssertNull("", spec with { Nullable = true });

            var bounded = spec with { Min = 0, Max = 1 };
            Assert.Equal([0m, 1m], Fails("1,5", bounded, "cfgerr.range"));
            Assert.Equal([0m], Fails("-0.1", spec with { Min = 0 }, "cfgerr.min"));
            Assert.Equal(1m, (decimal)Parse("1", bounded));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    // ---- текст

    [Fact]
    public void Text_EmptyInput_IsNullOnlyForNullableFields()
    {
        var plain = Spec(ConfigValueKind.Text);
        var nullable = plain with { Nullable = true };

        Assert.Equal("", (string?)Parse("", plain));
        Assert.Equal(JTokenType.String, Parse(null, plain).Type);
        AssertNull("", nullable);
        AssertNull(null, nullable);
        Assert.Equal(" с пробелами ", (string?)Parse(" с пробелами ", nullable));
        Assert.Equal("2026-09-07T07:42:31Z", (string?)Parse("2026-09-07T07:42:31Z", plain));
        Assert.Equal(JTokenType.String, Parse("2026-09-07T07:42:31Z", plain).Type);
    }

    [Fact]
    public void Text_RequiredFields_RejectEmpty()
    {
        Fails("", Known("ServerName"), "cfgerr.required");
        Fails("   ", Known("ServerName"), "cfgerr.required");
        Fails("", Known("WorldConfig.WorldName"), "cfgerr.required");
        Fails("", Known("WorldConfig.SaveFileLocation"), "cfgerr.required");
        Fails("", Known("ServerLanguage"), "cfgerr.notChoice"); // язык — выбор из списка
        Assert.Equal("My server", (string?)Parse("My server", Known("ServerName")));

        // остальным текстовым пустота разрешена
        Assert.Equal("", (string?)Parse("", Known("WorldConfig.PlayStyleLangCode")));
        Assert.Equal("", (string?)Parse("", Known("ModDbUrl")));
        AssertNull("", Known("Password"));
        AssertNull("", Known("WorldConfig.Seed"));
        AssertNull("", Known("MasterserverUrl"));
    }

    [Fact]
    public void EmptyIsNull_EmptyInputIsAlwaysNull()
    {
        var ip = Known("Ip");

        AssertNull("", ip);
        AssertNull("   ", ip); // адрес из пробелов — тоже «пусто»
        AssertNull(null, ip);
        Assert.Equal("192.168.0.10", (string?)Parse("192.168.0.10", ip));

        // в файле "" — нетронутое пустое поле всё равно даёт null, а не возвращает "" обратно
        Assert.True(ConfigValueCodec.TryParse("", ip, new JValue(""), out var token, out var errorKey, out _));
        Assert.Null(errorKey);
        Assert.Equal(JTokenType.Null, token!.Type);
        Assert.True(ConfigValueCodec.TryParse("  ", ip, new JValue("  "), out token, out _, out _));
        Assert.Equal(JTokenType.Null, token!.Type);

        // null и непустое значение возвращаются как были
        Assert.True(ConfigValueCodec.TryParse("", ip, JValue.CreateNull(), out token, out _, out _));
        Assert.Equal(JTokenType.Null, token!.Type);
        Assert.True(ConfigValueCodec.TryParse("10.0.0.1", ip, new JValue("10.0.0.1"), out token, out _, out _));
        Assert.Equal("10.0.0.1", (string?)token);

        // признак работает и сам по себе, без Nullable
        AssertNull("", Spec(ConfigValueKind.Text) with { EmptyIsNull = true });

        // списки клиентских модов: пустой список игра сама пишет как null
        var blackList = Known("ModIdBlackList");
        AssertNull("", blackList);
        AssertNull(" \n ", blackList);
        Assert.True(ConfigValueCodec.TryParse("", blackList, new JArray(), out token, out _, out _));
        Assert.Equal(JTokenType.Null, token!.Type);
        Assert.True(JToken.DeepEquals(new JArray("a", "b"), Parse("a\nb", blackList)));

        // обычному Nullable-полю нетронутая "" остаётся ""
        Assert.True(ConfigValueCodec.TryParse("", Known("Password"), new JValue(""), out token, out _, out _));
        Assert.Equal(JTokenType.String, token!.Type);
    }

    [Fact]
    public void NeverNull_EmptyInputIsEmptyStringOrList()
    {
        // приветствие: null роняет сервер при входе игрока
        var welcome = Known("WelcomeMessage");
        Assert.Equal("", (string?)Parse("", welcome));
        Assert.Equal(JTokenType.String, Parse(null, welcome).Type);
        Assert.Equal("Hi, {0}!\nRules: /rules", (string?)Parse("Hi, {0}!\r\nRules: /rules", welcome));

        // в файле null — нетронутое пустое поле даёт "", а не возвращает null обратно
        Assert.True(ConfigValueCodec.TryParse("", welcome, JValue.CreateNull(), out var token, out var errorKey, out _));
        Assert.Null(errorKey);
        Assert.Equal("", (string?)token);
        // даже если описание поля кто-то сделал Nullable
        Assert.Equal(JTokenType.String, Parse("", welcome with { Nullable = true }).Type);

        // папки модов: null роняет сервер при загрузке модов
        var modPaths = Known("ModPaths");
        Assert.Equal(JTokenType.Array, Parse("", modPaths).Type);
        Assert.True(ConfigValueCodec.TryParse("", modPaths, JValue.CreateNull(), out token, out _, out _));
        Assert.Equal(JTokenType.Array, token!.Type);
        Assert.Empty(token);
        Assert.Equal(JTokenType.Array, Parse("", modPaths with { Nullable = true }).Type);

        // поля нет в файле вовсе — править нечего
        Assert.True(ConfigValueCodec.TryParse("", welcome, null, out token, out _, out _));
        Assert.Equal(JTokenType.Null, token!.Type);
    }

    [Fact]
    public void KnownNumericFields_EnforceGameLimits()
    {
        // ошибка игры: от 2048 МБ защита по свободному месту молча отключается
        var disk = Known("DieBelowDiskSpaceMb");
        Assert.Equal(2047, (int)Parse("2047", disk));
        Assert.Equal(0, (int)Parse("0", disk));
        Assert.Equal([0m, 2047m], Fails("2048", disk, "cfgerr.range"));
        Assert.Equal([0m, 2047m], Fails("4096", disk, "cfgerr.range"));
        Assert.Equal([0m, 2047m], Fails("-1", disk, "cfgerr.range"));

        // uint в игре: больше int, но не больше 4294967295
        var split = Known("LogFileSplitAfterLine");
        Assert.Equal(4294967295, (long)Parse("4294967295", split));
        Assert.Equal([0m, 4294967295m], Fails("4294967296", split, "cfgerr.range"));

        Assert.Equal(16384, (int)Parse("16384", Known("MapSizeY")));
        Assert.Equal([0m, 16384m], Fails("16385", Known("MapSizeY"), "cfgerr.range"));
        Assert.Equal([0m, 67108864m], Fails("67108865", Known("MapSizeX"), "cfgerr.range"));

        // дробные
        Assert.Equal("33.333332", Parse("33.333332", Known("TickTime")).ToString(Formatting.None));
        Assert.Equal([1m], Fails("0.5", Known("TickTime"), "cfgerr.min"));
        Assert.Equal(0m, (decimal)Parse("0", Known("SpawnCapPlayerScaling")));
        Assert.Equal([0m], Fails("-1", Known("AntiAbuseBlockBurstAbuseBanDays"), "cfgerr.min"));

        // числовой выбор
        Assert.Equal(JTokenType.Integer, Parse("1", Known("AntiAbuse")).Type);
        Fails("3", Known("AntiAbuse"), "cfgerr.notChoice");
    }

    [Fact]
    public void Multiline_NormalizesLineEndings_PathIsTrimmed()
    {
        Assert.Equal("/op me\n/time set day", (string?)Parse("/op me\r\n/time set day", Known("StartupCommands")));
        AssertNull("", Known("StartupCommands"));
        Assert.Equal(@"D:\Saves\world.vcdbs", (string?)Parse(@"  D:\Saves\world.vcdbs ", Known("WorldConfig.SaveFileLocation")));
    }

    // ---- выбор и переключатель

    [Fact]
    public void Choice_AcceptsOnlyListedValues()
    {
        var style = Known("WorldConfig.PlayStyle");
        Assert.Equal("exploration", (string?)Parse("exploration", style));
        Fails("Exploration", style, "cfgerr.notChoice");
        Fails("", style, "cfgerr.notChoice");

        var whitelist = Known("WhitelistMode");
        var mode = Parse("2", whitelist);
        Assert.Equal(JTokenType.Integer, mode.Type);
        Assert.Equal(2, (int)mode);
        Fails("3", whitelist, "cfgerr.notChoice");
        Fails("on", whitelist with { Choices = [new("on", "on")] }, "cfgerr.notChoice"); // числовой выбор не из числа

        AssertNull("", style with { Nullable = true });
        Assert.Equal("", (string?)Parse("", style with { Choices = [new("", ""), .. style.Choices] })); // "" из файла — тоже вариант
    }

    [Fact]
    public void Bool_ParsesTrueAndFalse()
    {
        var spec = Spec(ConfigValueKind.Bool);

        Assert.True((bool)Parse("true", spec));
        Assert.False((bool)Parse(" False ", spec));
        Assert.Equal(JTokenType.Boolean, Parse("true", spec).Type);
        Fails("yes", spec, "cfgerr.notChoice");
        Fails("", spec, "cfgerr.required");
        AssertNull("", spec with { Nullable = true });
    }

    // ---- списки и сырой JSON

    [Fact]
    public void StringList_OneItemPerNonEmptyLine()
    {
        var spec = Spec(ConfigValueKind.StringList);

        var list = Parse("Mods\r\n\r\n  D:\\Shared Mods  \n\n", spec);
        Assert.True(JToken.DeepEquals(new JArray("Mods", "D:\\Shared Mods"), list));
        Assert.Equal("Mods\nD:\\Shared Mods", ConfigValueCodec.ToText(list, spec));

        var empty = Parse(" \n ", spec);
        Assert.Equal(JTokenType.Array, empty.Type);
        Assert.Empty(empty);
        AssertNull("", spec with { Nullable = true });
        Assert.Single(Parse("one", spec with { Nullable = true }));
    }

    [Fact]
    public void Json_AcceptsAnyValidValue()
    {
        var spec = Spec(ConfigValueKind.Json);

        AssertNull("null", spec);
        AssertNull("", spec);
        AssertNull("  ", spec);
        Assert.Equal(12, (int)Parse("12", spec));
        Assert.Equal(JTokenType.Boolean, Parse("true", spec).Type);
        Assert.Equal("text", (string?)Parse("\"text\"", spec));
        Assert.Empty(Parse("[ ]", spec));
        Assert.True(JToken.DeepEquals(new JObject { ["a"] = new JArray(1, 2), ["b"] = JValue.CreateNull() }, Parse("{ \"a\": [1, 2],\r\n \"b\": null }", spec)));

        // как и весь конфиг: дробные — decimal, даты — строки
        Assert.Equal("33.333332", Parse("33.333332", spec).ToString(Formatting.None));
        Assert.Equal(JTokenType.String, Parse("\"2026-09-07T07:42:31Z\"", spec).Type);
        Assert.Equal(JTokenType.String, Parse("{ \"at\": \"2026-09-07T07:42:31Z\" }", spec)["at"]!.Type);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("text without quotes")]
    [InlineData("[1, 2")]
    [InlineData("1 2")]
    [InlineData("{} {}")]
    [InlineData("{ \"a\": }")]
    public void Json_RejectsBrokenInput_WithReaderMessage(string text)
    {
        var args = Fails(text, Spec(ConfigValueKind.Json), "cfgerr.badJson");

        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(Assert.Single(args))));
    }

    // ---- разбор с исходным значением

    [Fact]
    public void WithOriginal_UntouchedTextReturnsOriginalToken()
    {
        // без исходного: "" в Nullable-поле стало бы null, список потерял бы пустой элемент
        var nullableText = Spec(ConfigValueKind.Text) with { Nullable = true };
        Assert.True(ConfigValueCodec.TryParse("", nullableText, new JValue(""), out var kept, out _, out _));
        Assert.Equal(JTokenType.String, kept!.Type);

        var list = new JArray("a", "", " b ");
        var listSpec = Spec(ConfigValueKind.StringList);
        var shown = ConfigValueCodec.ToText(list, listSpec);
        Assert.True(ConfigValueCodec.TryParse(shown.Replace("\n", "\r\n"), listSpec, list, out var sameList, out _, out _));
        Assert.True(JToken.DeepEquals(list, sameList));
        Assert.NotSame(list, sameList);

        // значение, которое не прошло бы проверку, но уже лежит в файле
        Assert.True(ConfigValueCodec.TryParse("0", Known("Port"), new JValue(0), out var zero, out _, out _));
        Assert.Equal(0, (int)zero!);

        // нет поля вовсе и текст пуст — null
        Assert.True(ConfigValueCodec.TryParse("", Spec(ConfigValueKind.Integer), null, out var none, out _, out _));
        Assert.Equal(JTokenType.Null, none!.Type);
    }

    [Fact]
    public void WithOriginal_ChangedTextIsParsedAsUsual()
    {
        var port = Known("Port");

        Assert.True(ConfigValueCodec.TryParse("8080", port, new JValue(42420), out var token, out _, out _));
        Assert.Equal(8080, (int)token!);

        Assert.False(ConfigValueCodec.TryParse("0", port, new JValue(42420), out token, out var errorKey, out var errorArgs));
        Assert.Null(token);
        Assert.Equal("cfgerr.range", errorKey);
        Assert.Equal([1m, 65535m], errorArgs);

        Assert.True(ConfigValueCodec.TryParse("", Spec(ConfigValueKind.Text) with { Nullable = true }, new JValue("was"), out token, out _, out _));
        Assert.Equal(JTokenType.Null, token!.Type);
    }

    [Fact]
    public void ReadOnly_IsNeverParsed_OnlyReturnedAsIs()
    {
        var spec = Known("ServerIdentifier");
        var original = new JValue("00000000-0000-0000-0000-000000000000");

        Fails("00000000-0000-0000-0000-000000000000", spec, "cfgerr.readOnly");
        Assert.True(ConfigValueCodec.TryParse("edited", spec, original, out var token, out var errorKey, out _));
        Assert.Null(errorKey);
        Assert.True(JToken.DeepEquals(original, token));
    }

    // ---- тексты ошибок

    [Fact]
    public void EveryErrorKeyIsTranslated()
    {
        string[] keys =
        [
            "cfgerr.notInteger", "cfgerr.notNumber", "cfgerr.range", "cfgerr.min", "cfgerr.max", "cfgerr.required",
            "cfgerr.badJson", "cfgerr.notChoice", "cfgerr.readOnly", "cfgerr.roleCodeEmpty", "cfgerr.roleCodeTaken",
        ];

        foreach (var (code, _) in Loc.Available)
            Assert.Empty(keys.Except(Loc.Keys(code)));
    }
}

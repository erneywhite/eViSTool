using eViSTool.Core.Server.Config;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests;

/// <summary>Образцы serverconfig.json из TestData: тесты работают с копией во временной папке.</summary>
public sealed class ConfigSamples : IDisposable
{
    /// <summary>Конфиг живого сервера (личное заменено) и конфиг, который свежий сервер создал сам.</summary>
    public const string Real = "serverconfig-real.json", Default = "serverconfig-default.json";

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "evistool-tests-" + Guid.NewGuid().ToString("N"))).FullName;

    public string ConfigPath => Path.Combine(_root, "serverconfig.json");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    public ServerConfigDocument Open(string sample = Real)
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", sample), ConfigPath, overwrite: true);
        return ServerConfigDocument.Load(ConfigPath);
    }

    /// <summary>Документ из своего JSON.</summary>
    public ServerConfigDocument OpenText(string json)
    {
        File.WriteAllText(ConfigPath, json);
        return ServerConfigDocument.Load(ConfigPath);
    }
}

public sealed class ServerConfigDocumentTests : IDisposable
{
    private readonly ConfigSamples _samples = new();

    public void Dispose() => _samples.Dispose();

    private string ConfigFile => _samples.ConfigPath;

    private static KeyValuePair<string, string>[] Pairs(params (string Key, string Value)[] items) =>
        [.. items.Select(i => KeyValuePair.Create(i.Key, i.Value))];

    // ---- круг «загрузил → сохранил»

    [Theory]
    [InlineData(ConfigSamples.Real)]
    [InlineData(ConfigSamples.Default)]
    public void UntouchedSave_LeavesFileExactlyAsItWas(string sample)
    {
        var doc = _samples.Open(sample);
        var before = File.ReadAllBytes(ConfigFile);

        Assert.Equal(77, doc.Root.Count);
        Assert.False(doc.IsDirty);
        doc.Save();

        Assert.Equal(before, File.ReadAllBytes(ConfigFile));
        Assert.Equal(before, File.ReadAllBytes(ConfigFile + ".evistool.bak"));
        Assert.False(doc.IsDirty);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Save_KeepsTheLineBreaksOfTheFile(string newLine)
    {
        // конфиг с Linux на Windows и наоборот: сохранение не перекраивает переводы строк во всём файле
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", ConfigSamples.Real)).ReplaceLineEndings(newLine);
        var doc = _samples.OpenText(text);
        doc.Save();
        Assert.Equal(text, File.ReadAllText(ConfigFile));

        doc.Set("Port", 42421);
        doc.Save();
        var after = File.ReadAllText(ConfigFile);
        Assert.Contains("  \"Port\": 42421," + newLine, after);
        Assert.Equal(text.Split(newLine).Length, after.Split(newLine).Length);
        Assert.Equal(text.Count(c => c == '\r'), after.Count(c => c == '\r'));
    }

    [Fact]
    public void Save_AfterOneEdit_ChangesOnlyThatLine()
    {
        var doc = _samples.Open();
        var before = File.ReadAllLines(ConfigFile);

        doc.Set("Port", 42421);
        doc.Save();

        var after = File.ReadAllLines(ConfigFile);
        Assert.Equal(before.Length, after.Length);
        var changed = Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).ToList();
        Assert.Equal("  \"Port\": 42421,", after[Assert.Single(changed)]);

        // то, что портится первым: точность дробных, «.0», null
        Assert.Contains("  \"TickTime\": 33.333332,", after);
        Assert.Contains("  \"AntiAbuseBlockBurstAbuseBanDays\": 14.0,", after);
        Assert.Contains("  \"MasterserverUrl\": null,", after);

        var reloaded = ServerConfigDocument.Load(ConfigFile);
        Assert.Equal(33.333332m, Assert.IsType<decimal>(((JValue)reloaded.Get("TickTime")!).Value));
        Assert.Equal(doc.Root.Properties().Select(p => p.Name), reloaded.Root.Properties().Select(p => p.Name));
    }

    [Fact]
    public void DateLikeStrings_StayStrings()
    {
        const string json = """
            {
              "When": "2026-09-07T07:42:31Z",
              "Nested": { "At": "2026-09-07T07:42:31+03:00", "Day": "2026-09-07" },
              "Nothing": null
            }
            """;
        var doc = _samples.OpenText(json);

        Assert.Equal(JTokenType.String, doc.Get("When")!.Type);
        Assert.Equal(JTokenType.String, doc.Get("Nested.At")!.Type);
        doc.Save();

        var saved = File.ReadAllText(ConfigFile);
        Assert.Contains("\"When\": \"2026-09-07T07:42:31Z\"", saved);
        Assert.Contains("\"At\": \"2026-09-07T07:42:31+03:00\"", saved);
        Assert.Contains("\"Day\": \"2026-09-07\"", saved);
        Assert.Contains("\"Nothing\": null", saved);
    }

    [Fact]
    public void Load_BrokenOrMissingFile_Throws()
    {
        Assert.ThrowsAny<IOException>(() => ServerConfigDocument.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "no.json")));
        Assert.ThrowsAny<Newtonsoft.Json.JsonException>(() => _samples.OpenText("{ \"Port\": "));
        Assert.ThrowsAny<Newtonsoft.Json.JsonException>(() => _samples.OpenText("[1, 2]"));
    }

    // ---- Get / Set

    [Fact]
    public void Get_ReadsByDottedPath()
    {
        var doc = _samples.Open();

        Assert.Equal(42420, (int)doc.Get("Port")!);
        Assert.Equal("Test World", (string?)doc.Get("WorldConfig.WorldName"));
        Assert.Equal("commonish", (string?)doc.Get("WorldConfig.WorldConfiguration.surfacecopper"));

        Assert.Null(doc.Get("NoSuchField"));
        Assert.Null(doc.Get("WorldConfig.NoSuchField"));
        Assert.Null(doc.Get("Port.Inner"));

        // записанный null — не «поля нет»
        Assert.Equal(JTokenType.Null, doc.Get("MasterserverUrl")!.Type);
    }

    [Fact]
    public void Set_KeepsFieldOrder_AndCreatesMissingObjects()
    {
        var doc = _samples.Open();
        var names = doc.Root.Properties().Select(p => p.Name).ToList();
        var worldNames = ((JObject)doc.Root["WorldConfig"]!).Properties().Select(p => p.Name).ToList();

        doc.Set("Port", 1234);
        doc.Set("WorldConfig.WorldName", "Other");
        Assert.Equal(names, doc.Root.Properties().Select(p => p.Name));
        Assert.Equal(worldNames, ((JObject)doc.Root["WorldConfig"]!).Properties().Select(p => p.Name));
        Assert.Equal(1234, (int)doc.Root["Port"]!);
        Assert.Equal("Other", (string?)doc.Root["WorldConfig"]!["WorldName"]);

        doc.Set("Extra.Deep.Value", 5);
        Assert.Equal(5, (int)doc.Root["Extra"]!["Deep"]!["Value"]!);
        Assert.Equal([.. names, "Extra"], doc.Root.Properties().Select(p => p.Name));

        // null на пути заменяется объектом, а чужое значение — нет
        doc.Set("DefaultSpawn.x", 1);
        Assert.Equal(1, (int)doc.Get("DefaultSpawn.x")!);
        Assert.Throws<InvalidOperationException>(() => doc.Set("ServerName.x", 1));
        Assert.Equal("Test Server", (string?)doc.Get("ServerName"));
    }

    [Fact]
    public void Set_Null_WritesJsonNull_AndForeignTokenIsCopied()
    {
        var doc = _samples.Open();

        doc.Set("ServerUrl", null);
        Assert.Equal(JTokenType.Null, doc.Get("ServerUrl")!.Type);

        var paths = doc.Get("ModPaths")!;
        doc.Set("ModPathsCopy", paths);
        Assert.NotSame(paths, doc.Get("ModPathsCopy"));
        Assert.Same(paths, doc.Get("ModPaths"));
        Assert.True(JToken.DeepEquals(paths, doc.Get("ModPathsCopy")));
    }

    // ---- IsDirty / Save / Revert

    [Fact]
    public void IsDirty_ComparesContent_NotTheFactOfWriting()
    {
        var doc = _samples.Open();

        doc.Set("Port", 42420);
        doc.Set("TickTime", new JValue(33.333332m));
        doc.Set("WorldConfig.Seed", "");
        doc.Set("MasterserverUrl", null);
        Assert.False(doc.IsDirty);

        doc.Set("Port", 1);
        Assert.True(doc.IsDirty);
        doc.Set("Port", 42420);
        Assert.False(doc.IsDirty);

        // пустая строка и null — разные значения; целое и дробное — тоже
        doc.Set("WorldConfig.Seed", null);
        Assert.True(doc.IsDirty);
        doc.Set("WorldConfig.Seed", "");
        doc.Set("Port", new JValue(42420m));
        Assert.True(doc.IsDirty);
    }

    [Fact]
    public void Save_MakesDocumentClean_AndWritesChanges()
    {
        var doc = _samples.Open();
        doc.Set("ServerName", "Имя по-русски");
        doc.Set("New.Field", true);
        Assert.True(doc.IsDirty);

        doc.Save();

        Assert.False(doc.IsDirty);
        var reloaded = ServerConfigDocument.Load(ConfigFile);
        Assert.Equal("Имя по-русски", (string?)reloaded.Get("ServerName"));
        Assert.True((bool)reloaded.Get("New.Field")!);
        Assert.True(JToken.DeepEquals(doc.Root, reloaded.Root));

        // после сохранения «чисто» — это уже новое состояние
        doc.Set("ServerName", "Test Server");
        Assert.True(doc.IsDirty);
    }

    [Fact]
    public void Revert_RestoresLoadedState_InTheSameRoot()
    {
        var doc = _samples.Open();
        var root = doc.Root;
        var names = root.Properties().Select(p => p.Name).ToList();
        var staleRole = doc.Roles[0];

        doc.Set("Port", 1);
        doc.Set("Added", "x");
        doc.Root.Remove("Upnp");
        staleRole.Name = "Changed";
        doc.DuplicateRole(staleRole, "copy");
        doc.SetWorldSettings([]);
        Assert.True(doc.IsDirty);

        doc.Revert();

        Assert.False(doc.IsDirty);
        Assert.Same(root, doc.Root);
        Assert.Equal(names, doc.Root.Properties().Select(p => p.Name));
        Assert.Equal(42420, (int)doc.Get("Port")!);
        Assert.Equal(9, doc.Roles.Count);
        Assert.Equal("Survival Visitor", doc.Roles[0].Name);
        Assert.Equal(3, doc.WorldSettings.Count);

        // прежняя обёртка роли от документа отвязана, свежая — живая
        Assert.NotSame(staleRole, doc.Roles[0]);
        staleRole.Name = "Stale";
        Assert.False(doc.IsDirty);
        doc.Roles[0].Name = "Fresh";
        Assert.True(doc.IsDirty);
    }

    [Fact]
    public void Revert_AfterSave_ReturnsToSavedState()
    {
        var doc = _samples.Open();
        doc.Set("Port", 1);
        doc.Save();
        doc.Set("Port", 2);

        doc.Revert();

        Assert.Equal(1, (int)doc.Get("Port")!);
        Assert.False(doc.IsDirty);
    }

    // ---- ChangedOnDisk

    [Fact]
    public void ChangedOnDisk_SeesForeignWrites_ButNotOwnSave()
    {
        var doc = _samples.Open();
        Assert.False(doc.ChangedOnDisk());

        doc.Set("Port", 1);
        doc.Save();
        Assert.False(doc.ChangedOnDisk());

        // сервер переписал файл: другая длина
        File.AppendAllText(ConfigFile, " ");
        Assert.True(doc.ChangedOnDisk());

        doc.Save();
        Assert.False(doc.ChangedOnDisk());

        // та же длина, другое время записи
        File.SetLastWriteTimeUtc(ConfigFile, File.GetLastWriteTimeUtc(ConfigFile).AddSeconds(5));
        Assert.True(doc.ChangedOnDisk());

        doc.Save();
        File.Delete(ConfigFile);
        Assert.True(doc.ChangedOnDisk());
    }

    // ---- роли

    [Fact]
    public void Roles_ReadStandardRolesAsInFile()
    {
        var doc = _samples.Open();
        var roles = doc.Roles;

        Assert.Equal(ServerConfigSchema.StandardRoleCodes, roles.Select(r => r.Code));
        Assert.All(roles, r => Assert.True(r.IsStandard));
        Assert.Equal("suplayer", doc.DefaultRoleCode);

        var admin = roles[^1];
        Assert.Equal("Admin", admin.Name);
        Assert.Equal("Has all privileges, including giving other players admin status.", admin.Description);
        Assert.Equal(99999, admin.PrivilegeLevel);
        Assert.Equal(1, admin.DefaultGameMode);
        Assert.Equal("LightBlue", admin.Color);
        Assert.Equal(int.MaxValue, admin.LandClaimAllowance);
        Assert.Equal(99999, admin.LandClaimMaxAreas);
        Assert.Equal((5, 5, 5), (admin.LandClaimMinX, admin.LandClaimMinY, admin.LandClaimMinZ));
        Assert.True(admin.AutoGrant);
        Assert.False(roles[0].AutoGrant);
        Assert.Equal(["chat"], roles[0].Privileges);

        // у админа есть всё, что знает программа, и ничего сверх
        Assert.Equal(ServerConfigSchema.KnownPrivileges.Order(), admin.Privileges.Order());
    }

    [Fact]
    public void Roles_AreLiveWrappers_OnePerRole()
    {
        var doc = _samples.Open();
        var role = doc.Roles[4];

        Assert.Same(role, doc.Roles[4]);
        Assert.Same(doc.Root["Roles"]![4], role.Json);

        role.Name = "Игрок";
        role.PrivilegeLevel = 7;
        role.LandClaimMinY = 9;
        role.AutoGrant = true;
        var json = (JObject)doc.Root["Roles"]![4]!;
        Assert.Equal("Игрок", (string?)json["Name"]);
        Assert.Equal(7, (int)json["PrivilegeLevel"]!);
        Assert.Equal(9, (int)json["LandClaimMinSize"]!["Y"]!);
        Assert.True((bool)json["AutoGrant"]!);
        Assert.True(doc.IsDirty);

        role.Name = "Survival Player";
        role.PrivilegeLevel = 0;
        role.LandClaimMinY = 5;
        role.AutoGrant = false;
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void Role_MissingFields_ReadAsDefaults_AndWritingCreatesThem()
    {
        var doc = _samples.OpenText("""{ "Roles": [ { "Code": "bare", "LandClaimMinSize": null, "Custom": { "kept": 1 } }, "junk" ] }""");
        var role = Assert.Single(doc.Roles);

        Assert.Equal("bare", role.Code);
        Assert.Equal(("", "", ""), (role.Name, role.Description, role.Color));
        Assert.Equal((0, 0, 0, 0), (role.PrivilegeLevel, role.DefaultGameMode, role.LandClaimAllowance, role.LandClaimMaxAreas));
        Assert.Equal((0, 0, 0), (role.LandClaimMinX, role.LandClaimMinY, role.LandClaimMinZ));
        Assert.False(role.AutoGrant);
        Assert.Empty(role.Privileges);
        Assert.False(role.IsStandard);

        // то же значение, что и так читается, — поле не появляется
        role.Name = "";
        role.LandClaimAllowance = 0;
        role.LandClaimMinX = 0;
        role.AutoGrant = false;
        Assert.False(doc.IsDirty);

        role.Name = "Bare";
        role.LandClaimAllowance = 100;
        role.DefaultGameMode = 2;
        role.AutoGrant = true;
        role.LandClaimMinZ = 7;
        role.SetPrivilege("chat", true);

        Assert.Equal("Bare", (string?)role.Json["Name"]);
        Assert.Equal(100, (int)role.Json["LandClaimAllowance"]!);
        Assert.Equal(2, (int)role.Json["DefaultGameMode"]!);
        Assert.True((bool)role.Json["AutoGrant"]!);
        Assert.True(JToken.DeepEquals(new JObject { ["X"] = 0, ["Y"] = 0, ["Z"] = 7 }, role.Json["LandClaimMinSize"]));
        Assert.Equal(["chat"], role.Privileges);

        // незнакомое поле роли и мусор в массиве остались как были
        Assert.Equal(1, (int)role.Json["Custom"]!["kept"]!);
        Assert.Equal("junk", (string?)doc.Root["Roles"]![1]);
    }

    [Fact]
    public void SetPrivilege_AddsToEnd_RemovesWithoutTouchingTheRest()
    {
        var doc = _samples.Open();
        var role = doc.Roles[2]; // limitedsuplayer
        var before = role.Privileges.ToList();

        role.SetPrivilege("chat", true); // уже есть
        Assert.Equal(before, role.Privileges);
        Assert.False(doc.IsDirty);

        role.SetPrivilege("tp", true);
        role.SetPrivilege("modprivilege", true); // привилегия от мода — тоже можно
        role.SetPrivilege("tp", true);
        Assert.Equal([.. before, "tp", "modprivilege"], role.Privileges);

        role.SetPrivilege("chat", false);
        role.SetPrivilege("nosuch", false);
        Assert.Equal([.. before.Where(p => p != "chat"), "tp", "modprivilege"], role.Privileges);

        // дубликаты из файла уходят разом
        var dup = (JArray)role.Json["Privileges"]!;
        dup.Add("build");
        role.SetPrivilege("build", false);
        Assert.DoesNotContain("build", role.Privileges);
    }

    [Fact]
    public void DuplicateRole_MakesIndependentCopyAtTheEnd()
    {
        var doc = _samples.Open();
        var source = doc.Roles[4]; // suplayer
        source.Json["ModField"] = new JObject { ["x"] = 1 };

        var copy = doc.DuplicateRole(source, "  vip ");

        Assert.Equal(10, doc.Roles.Count);
        Assert.Same(copy, doc.Roles[^1]);
        Assert.Equal("vip", copy.Code);
        Assert.Equal("Survival Player", copy.Name);
        Assert.False(copy.IsStandard);
        Assert.Equal(source.Privileges, copy.Privileges);
        Assert.Equal(source.LandClaimAllowance, copy.LandClaimAllowance);
        Assert.Equal(1, (int)copy.Json["ModField"]!["x"]!);
        Assert.Equal("suplayer", source.Code);

        // копия глубокая: правки не задевают исходную роль
        copy.SetPrivilege("tp", true);
        copy.LandClaimMinX = 1;
        copy.Json["ModField"]!["x"] = 2;
        Assert.DoesNotContain("tp", source.Privileges);
        Assert.Equal(5, source.LandClaimMinX);
        Assert.Equal(1, (int)source.Json["ModField"]!["x"]!);

        var named = doc.DuplicateRole(copy, "vip2", " Важный гость ");
        Assert.Equal("Важный гость", named.Name);
        Assert.Contains("tp", named.Privileges);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("admin")]
    [InlineData("ADMIN")]
    [InlineData(" SuPlayer ")]
    public void DuplicateRole_RejectsEmptyOrTakenCode(string code)
    {
        var doc = _samples.Open();

        var ex = Assert.Throws<InvalidOperationException>(() => doc.DuplicateRole(doc.Roles[0], code));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain("cfgerr.", ex.Message);
        Assert.Equal(9, doc.Roles.Count);
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void RemoveRole_OnlyCustomAndNotDefault()
    {
        var doc = _samples.Open();
        var vip = doc.DuplicateRole(doc.Roles[4], "vip");
        var guest = doc.DuplicateRole(doc.Roles[0], "guest");

        Assert.All(doc.Roles.Take(9), r => Assert.False(doc.RemoveRole(r)));

        doc.DefaultRoleCode = "VIP";
        Assert.False(doc.RemoveRole(vip));
        Assert.Equal(11, doc.Roles.Count);

        Assert.True(doc.RemoveRole(guest));
        Assert.False(doc.RemoveRole(guest)); // уже не в документе
        Assert.Equal(ServerConfigSchema.StandardRoleCodes.Append("vip"), doc.Roles.Select(r => r.Code));

        doc.DefaultRoleCode = "suplayer";
        Assert.True(doc.RemoveRole(vip));
        Assert.False(doc.IsDirty);

        // роль другого документа — не наша
        Assert.False(doc.RemoveRole(new RoleEntry(new JObject { ["Code"] = "stranger" })));
    }

    [Fact]
    public void DefaultRoleCode_ReadsAndWrites()
    {
        var doc = _samples.Open();

        doc.DefaultRoleCode = "suplayer";
        Assert.False(doc.IsDirty);

        doc.DefaultRoleCode = "crplayer";
        Assert.Equal("crplayer", (string?)doc.Root["DefaultRoleCode"]);
        Assert.True(doc.IsDirty);

        doc.DefaultRoleCode = null;
        Assert.Null(doc.DefaultRoleCode);
        Assert.Equal(JTokenType.Null, doc.Root["DefaultRoleCode"]!.Type);

        Assert.Null(_samples.OpenText("{}").DefaultRoleCode);
    }

    // ---- настройки мира

    [Fact]
    public void WorldSettings_ReadInFileOrder_ValuesAsWritten()
    {
        Assert.Equal(
            Pairs(("surfacecopper", "commonish"), ("surfacetin", "commonish"), ("colorAccurateWorldmap", "true")),
            _samples.Open().WorldSettings);

        Assert.Empty(_samples.Open(ConfigSamples.Default).WorldSettings);
        Assert.Empty(_samples.OpenText("{}").WorldSettings);

        var mixed = _samples.OpenText("""{ "WorldConfig": { "WorldConfiguration": { "flag": true, "off": false, "ratio": 0.50, "count": 3, "text": "x", "none": null } } }""");
        Assert.Equal(
            Pairs(("flag", "true"), ("off", "false"), ("ratio", "0.50"), ("count", "3"), ("text", "x"), ("none", "")),
            mixed.WorldSettings);
    }

    [Fact]
    public void SetWorldSettings_WritesStringsInGivenOrder()
    {
        var doc = _samples.Open();

        doc.SetWorldSettings([new("colorAccurateWorldmap", "false"), new(" newKey ", "12"), new("", "skipped"), new("surfacecopper", "commonish")]);

        var json = (JObject)doc.Get(ServerConfigSchema.WorldSettingsPath)!;
        Assert.Equal(["colorAccurateWorldmap", "newKey", "surfacecopper"], json.Properties().Select(p => p.Name));
        Assert.All(json.Properties(), p => Assert.Equal(JTokenType.String, p.Value.Type));
        Assert.Equal("false", (string?)json["colorAccurateWorldmap"]);
        Assert.Equal("12", (string?)json["newKey"]);
        Assert.True(doc.IsDirty);

        // WorldConfiguration осталась на своём месте в WorldConfig
        Assert.Equal("WorldConfiguration", ((JObject)doc.Root["WorldConfig"]!).Properties().ElementAt(7).Name);
    }

    [Fact]
    public void SetWorldSettings_SameList_ChangesNothing()
    {
        var doc = _samples.Open();

        doc.SetWorldSettings(doc.WorldSettings);

        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void SetWorldSettings_KeysDifferingByCase_AreDifferentKeys_ExactDuplicateLastWins()
    {
        var doc = _samples.Open();

        doc.SetWorldSettings([new("Key", "1"), new("key", "2"), new("other", "3"), new("Key", "4")]);

        Assert.Equal(Pairs(("Key", "4"), ("key", "2"), ("other", "3")), doc.WorldSettings);
    }

    [Fact]
    public void SetWorldSettings_UntouchedValueKeepsItsJsonType()
    {
        var doc = _samples.OpenText("""{ "WorldConfig": { "WorldConfiguration": { "flag": true, "count": 3, "text": "x" } } }""");

        doc.SetWorldSettings([new("flag", "true"), new("count", "4"), new("text", "x")]);

        var json = (JObject)doc.Get(ServerConfigSchema.WorldSettingsPath)!;
        Assert.Equal(JTokenType.Boolean, json["flag"]!.Type);
        Assert.Equal(JTokenType.String, json["count"]!.Type);
        Assert.Equal("4", (string?)json["count"]);
    }

    [Fact]
    public void SetWorldSettings_EmptyList_KeepsNullIfItWasNull_OtherwiseEmptyObject()
    {
        // было null: добавили и убрали — снова null, документ чистый
        var fresh = _samples.Open(ConfigSamples.Default);
        fresh.SetWorldSettings([new("a", "1")]);
        Assert.True(fresh.IsDirty);
        fresh.SetWorldSettings([]);
        Assert.Equal(JTokenType.Null, fresh.Get(ServerConfigSchema.WorldSettingsPath)!.Type);
        Assert.False(fresh.IsDirty);

        // был объект — остаётся объект
        var real = _samples.Open();
        real.SetWorldSettings([]);
        var json = Assert.IsType<JObject>(real.Get(ServerConfigSchema.WorldSettingsPath));
        Assert.Empty(json.Properties());
        Assert.Empty(real.WorldSettings);

        // поля не было вовсе — и не появляется
        var bare = _samples.OpenText("""{ "WorldConfig": { "WorldName": "w" } }""");
        bare.SetWorldSettings([new("a", "1")]);
        bare.SetWorldSettings([]);
        Assert.Null(bare.Get(ServerConfigSchema.WorldSettingsPath));
        Assert.False(bare.IsDirty);

        var empty = _samples.OpenText("{}");
        empty.SetWorldSettings([]);
        Assert.False(empty.IsDirty);
        Assert.Null(empty.Get("WorldConfig"));
    }
}

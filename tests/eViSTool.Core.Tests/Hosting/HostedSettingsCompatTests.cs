using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;
using eViSTool.Core.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// Совместимость settings.json с профилем «Сервер у хостера» (проект, п. 1.4): старые настройки читаются как раньше,
/// новые не теряются, незнакомые значения не сбрасывают все профили, а метка «hosted» не пускает старое окно
/// в локальные папки.
/// </summary>
public sealed class HostedSettingsCompatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evistool-hostedcompat-" + Guid.NewGuid().ToString("N"));

    public HostedSettingsCompatTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Settings(string json)
    {
        var file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(file, json);
        return file;
    }

    private const string OldSettings = """
        {
          "Profiles": [
            { "Id": "a1", "Name": "Мой клиент", "Kind": "Client", "GameDir": "C:\\Games\\VS", "DataDir": "C:\\Data" },
            { "Id": "b2", "Name": "Дальний", "Kind": "Server", "RemoteCode": "AQAAANCMnd8BFdERjHoAwE" }
          ],
          "ActiveProfileId": "b2",
          "Language": "ru"
        }
        """;

    [Fact]
    public void OldSettings_WithoutHosted_ReadAsBefore_AndSaveWithoutNewFields()
    {
        var store = new SettingsStore(Settings(OldSettings));
        var settings = store.Load();

        Assert.Equal(["a1", "b2"], settings.Profiles.Select(p => p.Id));
        Assert.Equal("b2", settings.ActiveProfileId);
        var (client, agent) = (settings.Profiles[0], settings.Profiles[1]);
        Assert.Equal((ProfileHost.Local, true, false, false), (client.Host, client.IsLocal, client.IsRemote, client.IsHosted));
        Assert.Equal((ProfileHost.Agent, false, true, false), (agent.Host, agent.IsLocal, agent.IsRemote, agent.IsHosted));
        Assert.Null(client.Hosted);
        Assert.Null(agent.Hosted);
        Assert.False(File.Exists(store.Path + ".broken"));

        store.Save(settings);
        var saved = File.ReadAllText(store.Path);
        Assert.DoesNotContain("\"Hosted\"", saved); // поле пустое — в файл не пишется, старое окно ничего нового не увидит
        Assert.DoesNotContain("\"Host\"", saved);   // вычисляемые свойства в файл не идут
        Assert.DoesNotContain("IsLocal", saved);
    }

    [Fact]
    public void HostedProfile_RoundTripsWithoutLoss()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var settings = new AppSettings();
        var hosted = new GameProfile
        {
            Name = "G-Portal", Kind = ProfileKind.Server, RemoteCode = GameProfile.HostedMarker,
            Hosted = new HostedConnection
            {
                Protocol = HostedProtocol.Ftp, Host = "198.51.100.7", Port = 58021, Tls = FtpTls.None, User = "gp_user",
                Password = HostedSecret.Protect("s3cret-Пароль"), ActiveMode = true, Root = "/g123456",
                ConfigPath = "serverconfig.json", ModDirs = ["Mods", "gportal_mods"], InstallDir = "Mods", GameDir = ".",
                GameVersion = "1.22.7", CertificateSha256 = new string('a', 64), SkipStopReminder = true,
            },
        };
        settings.Profiles.Add(hosted);
        settings.ActiveProfileId = hosted.Id;
        store.Save(settings);

        var text = File.ReadAllText(store.Path);
        Assert.DoesNotContain("s3cret", text); // пароль только зашифрованным
        Assert.Contains("\"Protocol\": \"Ftp\"", text);
        Assert.Contains("\"Tls\": \"None\"", text);

        var back = store.Load().Profiles.Single();
        Assert.True(back.IsHosted);
        Assert.Equal(ProfileKind.Server, back.Kind);
        Assert.Equal(JsonConvert.SerializeObject(hosted.Hosted), JsonConvert.SerializeObject(back.Hosted));
        Assert.Equal("s3cret-Пароль", HostedSecret.Unprotect(back.Hosted!.Password));
    }

    [Fact]
    public void UnknownEnumValues_BecomeDefaults_OtherProfilesStay()
    {
        var store = new SettingsStore(Settings("""
            {
              "Profiles": [
                { "Id": "a1", "Name": "Клиент", "Kind": "Client" },
                { "Id": "h1", "Name": "Хостер", "Kind": "Server", "RemoteCode": "hosted",
                  "Hosted": { "Protocol": "Telnet", "Host": "203.0.113.5", "Port": 2121, "Tls": 42, "User": "u", "FutureField": [1, 2] } },
                { "Id": "h2", "Name": "Хостер 2", "Kind": "Server", "RemoteCode": "hosted",
                  "Hosted": { "Protocol": { "name": "Ssh" }, "Host": "203.0.113.6", "Tls": "Quantum", "User": "v" } },
                { "Id": "h3", "Name": "Хостер 3", "Kind": "Server", "RemoteCode": "hosted",
                  "Hosted": { "Protocol": "Sftp", "Host": "203.0.113.7", "Tls": "Implicit", "Port": 22 } }
              ],
              "ActiveProfileId": "h1"
            }
            """));
        var settings = store.Load();

        Assert.False(File.Exists(store.Path + ".broken")); // файл не сочтён битым
        Assert.Equal(["a1", "h1", "h2", "h3"], settings.Profiles.Select(p => p.Id));
        var h1 = settings.Profiles[1].Hosted!;
        Assert.Equal((HostedProtocol.Ftp, FtpTls.Auto, "203.0.113.5", 2121, "u"), (h1.Protocol, h1.Tls, h1.Host, h1.Port, h1.User));
        var h2 = settings.Profiles[2].Hosted!;
        Assert.Equal((HostedProtocol.Ftp, FtpTls.Auto, "203.0.113.6", 21), (h2.Protocol, h2.Tls, h2.Host, h2.Port));
        var h3 = settings.Profiles[3].Hosted!;
        Assert.Equal((HostedProtocol.Sftp, FtpTls.Implicit), (h3.Protocol, h3.Tls)); // знакомые значения — как были
    }

    [Fact]
    public void MarkerWithoutConnection_IsHosted_WithoutTarget_AndTouchesNoLocalFiles()
    {
        // профиль сохранило старое окно: объекта подключения нет, осталась метка
        var profile = new GameProfile { Name = "Хостер", Kind = ProfileKind.Server, RemoteCode = GameProfile.HostedMarker };

        Assert.True(profile.IsHosted);
        Assert.False(profile.IsRemote);
        Assert.False(profile.IsLocal);
        Assert.Null(ModTarget.For(profile));
        HostedTestText.AssertIs("hosted.errNoConnection", ModTarget.UnreadableText(profile));

        var resolved = ProfileResolver.Resolve(profile);
        Assert.True(resolved.OffMachine);
        Assert.Empty(resolved.ModDirs);
        Assert.Null(resolved.ConfigPath);
        Assert.Empty(ModUpdateService.ScanLocal(resolved));
        Assert.Null(ServerMods.StampOf(resolved));
        Assert.Throws<InvalidOperationException>(() =>
            ModConfigEditor.SetEnabled(resolved, ModInfo.Parse("""{ "modid": "carryon", "name": "Carry On", "version": "1.0.0" }"""), false));
        Assert.Null(Game.PlayTargets.Own(profile));
    }

    [Fact]
    public void OldWindow_CannotReadTheMarker_AsAgentCode()
    {
        // гарантия для окон до 0.12: метку они примут за код агента, не расшифруют и покажут «код не читается»
        Assert.Null(RemoteSecret.Unprotect(GameProfile.HostedMarker));
    }

    [Fact]
    public void OffMachineGuard_DoesNotTouchLocalPathsOfAForeignResolvedProfile()
    {
        // пути агента, совпавшие с папкой на этой машине, по страховке не сканируются
        var mods = Path.Combine(_dir, "Mods");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, "x.dll"), "");
        var foreign = new ResolvedProfile { Profile = new GameProfile(), ModDirs = [mods], OffMachine = true };
        Assert.Empty(ModUpdateService.ScanLocal(foreign));
        Assert.Null(ServerMods.StampOf(foreign));

        var local = foreign with { OffMachine = false };
        Assert.Single(ModUpdateService.ScanLocal(local)); // без отметки — как всегда
        Assert.NotNull(ServerMods.StampOf(local));
    }

    [Fact]
    public void HostedTarget_SnapshotsSettings_AndDecryptsPassword()
    {
        var profile = HostedProfile("pa$$");
        var target = ModTarget.For(profile)!;

        Assert.True(target.IsHosted);
        Assert.False(target.IsRemote);
        Assert.False(target.IsLocal);
        Assert.Equal("pa$$", target.Hosted!.Endpoint.Password);
        Assert.Equal(("198.51.100.7", 58021, FtpTls.None), (target.Hosted.Endpoint.Host, target.Hosted.Endpoint.Port, target.Hosted.Endpoint.Tls));

        profile.Hosted!.Host = "203.0.113.99"; // правка профиля посреди операции цель не сдвигает
        profile.Hosted.ModDirs!.Add("other");
        Assert.Equal("198.51.100.7", target.Hosted.Settings.Host);
        Assert.Equal(["Mods"], target.Hosted.Settings.ModDirs!);
        Assert.Equal("198.51.100.7", target.Profile.Hosted!.Host);
    }

    [Fact]
    public void HostedTarget_WithoutSavedPassword_UsesEmpty_WithForeignPassword_IsUnreadable()
    {
        var noPassword = HostedProfile(null);
        Assert.Equal("", ModTarget.For(noPassword)!.Hosted!.Endpoint.Password);

        var foreign = HostedProfile(null);
        foreign.Hosted!.Password = "aes1:AAAA"; // зашифровано не здесь
        Assert.Null(ModTarget.For(foreign));
        HostedTestText.AssertIs("hosted.errDecrypt", ModTarget.UnreadableText(foreign));
    }

    [Fact]
    public void ExistingTargets_KeepTheirMeaning()
    {
        var local = ModTarget.For(new GameProfile { Name = "Клиент", Kind = ProfileKind.Client, DataDir = _dir })!;
        Assert.True(local.IsLocal);
        Assert.Null(local.Hosted);
        Assert.Equal(TimeSpan.FromSeconds(10), ModTargets.ScanTimeout(local));
        Assert.Equal("boom", ModTargets.Describe(local, new IOException("boom")));

        var code = new ConnectionCode("192.0.2.10", 42001, RemoteAccess.NewKey(), new string('c', 64));
        var agent = ModTarget.For(new GameProfile { Name = "Дальний", Kind = ProfileKind.Server, RemoteCode = RemoteSecret.Protect(code) })!;
        Assert.True(agent.IsRemote);
        Assert.False(agent.IsLocal);
        Assert.Equal(code, agent.Remote);
        HostedTestText.AssertIs("remote.errDecrypt", ModTarget.UnreadableText(agent.Profile));

        var hosted = ModTarget.For(HostedProfile("x"))!;
        Assert.Equal(TimeSpan.FromSeconds(45), ModTargets.ScanTimeout(hosted));
        HostedTestText.AssertIs("hosted.errAuth", ModTargets.Describe(hosted, new RemoteFilesException(RemoteFailure.Auth, "530")));
    }

    internal static GameProfile HostedProfile(string? password) => new()
    {
        Name = "G-Portal", Kind = ProfileKind.Server, RemoteCode = GameProfile.HostedMarker,
        Hosted = new HostedConnection
        {
            Host = "198.51.100.7", Port = 58021, Tls = FtpTls.None, User = "gp_user",
            Password = password is null ? null : HostedSecret.Protect(password), ModDirs = ["Mods"],
        },
    };

    [Fact]
    public void TolerantConverter_ReadsNullableAndKnownValues()
    {
        var ok = JsonConvert.DeserializeObject<Probe>("""{ "Tls": "Explicit", "Maybe": "Sftp", "Number": 2 }""")!;
        Assert.Equal((FtpTls.Explicit, HostedProtocol.Sftp, FtpTls.Explicit), (ok.Tls, ok.Maybe, ok.Number));

        var odd = JsonConvert.DeserializeObject<Probe>("""{ "Tls": "Nope", "Maybe": "Nope", "Number": 99, "After": "дальше" }""")!;
        Assert.Equal((FtpTls.Implicit, (HostedProtocol?)null, FtpTls.None, "дальше"), (odd.Tls, odd.Maybe, odd.Number, odd.After));

        var nulls = JsonConvert.DeserializeObject<Probe>("""{ "Tls": null, "Maybe": null }""")!;
        Assert.Equal((FtpTls.Implicit, (HostedProtocol?)null), (nulls.Tls, nulls.Maybe));

        // пишется строкой, как у StringEnumConverter
        Assert.Contains("\"Tls\":\"Implicit\"", JsonConvert.SerializeObject(new Probe()));
    }

    private sealed class Probe
    {
        [JsonConverter(typeof(TolerantEnumConverter))] public FtpTls Tls { get; set; } = FtpTls.Implicit; // своё «по умолчанию»
        [JsonConverter(typeof(TolerantEnumConverter))] public HostedProtocol? Maybe { get; set; }
        [JsonConverter(typeof(TolerantEnumConverter))] public FtpTls Number { get; set; }
        public string? After { get; set; }
    }

    [Fact]
    public void ProfileJson_HasMarkerAndNoPlainPassword()
    {
        var profile = HostedProfile("Very-Secret-42");
        var json = JObject.Parse(JsonConvert.SerializeObject(profile));
        Assert.Equal("hosted", json.Value<string>("RemoteCode"));
        Assert.DoesNotContain("Very-Secret-42", json.ToString());
        Assert.Null(json["IsHosted"]);
    }
}

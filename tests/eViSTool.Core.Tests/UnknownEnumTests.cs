using eViSTool.Core.Notifications;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Settings;
using Newtonsoft.Json;

namespace eViSTool.Core.Tests;

/// <summary>Аудит 4.6: значение перечисления из более новой версии не сбрасывает весь файл настроек.</summary>
public sealed class UnknownEnumTests
{
    [Fact]
    public void AppSettings_WithUnknownValues_KeepTheProfiles()
    {
        var json = """
            { "ProfileSort": "ByMood", "Profiles": [ { "Id": "p1", "Name": "Мой", "Kind": "Cloud" } ] }
            """;
        var s = JsonConvert.DeserializeObject<AppSettings>(json)!;
        Assert.Equal(ProfileSort.Created, s.ProfileSort);
        Assert.Equal("Мой", Assert.Single(s.Profiles).Name);
    }

    [Fact]
    public void Automation_UnknownRestartMode_IsOff()
    {
        var a = JsonConvert.DeserializeObject<ServerAutomation>("""{ "RestartMode": "Weekly", "BackupEnabled": true }""")!;
        Assert.Equal(RestartMode.Off, a.RestartMode);
        Assert.True(a.BackupEnabled);
    }

    [Fact]
    public void NotifyRoutes_SkipUnknownEvents_AndRoundTrip()
    {
        var known = Enum.GetValues<NotifyEvent>()[0];
        var json = $$"""{ "ServerName": "S", "Routes": { "{{known}}": ["a"], "SolarEclipse": ["b"], "7": ["c"] } }""";
        var s = JsonConvert.DeserializeObject<ServerNotifySettings>(json)!;
        Assert.Equal("S", s.ServerName);
        Assert.Equal(["a"], Assert.Single(s.Routes).Value);

        var again = JsonConvert.DeserializeObject<ServerNotifySettings>(JsonConvert.SerializeObject(s))!;
        Assert.Equal(["a"], again.Routes[known]);
    }
}

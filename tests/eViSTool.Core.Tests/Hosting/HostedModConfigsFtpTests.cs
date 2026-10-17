using System.Text;
using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Hosting;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// «Настройки модов» сервера у хостера по настоящему протоколу: адаптер FluentFTP на <see cref="MiniFtpServer"/> «как
/// G-Portal» (поверх не переименовывает, MLSD без FEAT, NOOP не реализован) и «как vsftpd» (без MLSD, LIST с местным
/// временем). Полный круг: список, чтение, запись, «поменяли с момента открытия», сброс и шаг назад.
/// </summary>
public sealed class HostedModConfigsFtpTests
{
    public static TheoryData<string> Modes => ["gportal", "vsftpd"];

    [Theory, MemberData(nameof(Modes))]
    public async Task FullCircle_OverRealFtp(string mode)
    {
        await using var server = new MiniFtpServer(mode == "gportal" ? MiniFtpOptions.GPortal() : MiniFtpOptions.Vsftpd());
        var old = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        server.Put("/VintagestoryServer.dll", [1, 2, 3]);
        server.Put("/version.txt", Encoding.UTF8.GetBytes("linux-x64_1.22.7"));
        server.Put("/serverconfig.json", Encoding.UTF8.GetBytes("{ \"ModPaths\": [\"Mods\"], \"WorldConfig\": { \"DisabledMods\": null } }"));
        server.Put("/Mods/carryon_1.0.0.zip", HostedFixture.ZipBytes("carryon", "1.0.0", "Carry On"));
        server.Put("/ModConfig/carryon.json", Encoding.UTF8.GetBytes("{ \"Range\": 3 }"), old);
        server.Put("/ModConfig/AutoMapMarkers/Русский.json", Encoding.UTF8.GetBytes("{}"), old);

        // свой адрес на тест (тесты Core идут параллельно), соединение — настоящий адаптер к этому серверу
        var host = "mcfg-" + Guid.NewGuid().ToString("N")[..12] + ".test";
        using var registration = RemoteFilesFactory.RegisterHost(host, e =>
            new FluentFtpFiles(e with { Host = "127.0.0.1", Port = server.Port, Timeout = TimeSpan.FromSeconds(5) }));
        var profile = new GameProfile
        {
            Id = "mcfg-" + Guid.NewGuid().ToString("N"), Name = "Hosted", Kind = ProfileKind.Server, RemoteCode = GameProfile.HostedMarker,
            Hosted = new HostedConnection { Host = host, Port = server.Port, User = server.Options.User, Password = HostedSecret.Protect(server.Options.Password) },
        };
        try
        {
            var source = new HostedModConfigSource(ModTarget.For(profile)!);

            var files = await source.ListAsync();
            Assert.Equal(["carryon.json", "AutoMapMarkers/Русский.json"], files.Select(f => f.RelativePath));
            Assert.Equal("carryon", files[0].ModId);

            var opened = await source.ReadAsync("carryon.json");
            Assert.Equal("{ \"Range\": 3 }", opened.Text);
            Assert.NotNull(opened.ChangedUtc);

            var saved = await source.SaveAsync(new ModConfigSaveRequest("carryon.json", "{ \"Range\": 5 }", opened.ChangedUtc));
            Assert.False(saved.Changed);
            Assert.Equal("{ \"Range\": 5 }", File.ReadAllText(server.Local("/ModConfig/carryon.json")));
            Assert.Equal(["AutoMapMarkers", "carryon.json"], Directory.EnumerateFileSystemEntries(server.Local("/ModConfig"))
                .Select(Path.GetFileName).Order(StringComparer.Ordinal)); // ни tmp, ни bak
            Assert.Equal(mode == "gportal" ? 3 : 1, server.Count("RNFR")); // G-Portal — через bak
            Assert.Equal(0, server.Count("NOOP"));

            // мод переписал файл, пока он был открыт
            File.SetLastWriteTimeUtc(server.Local("/ModConfig/carryon.json"), DateTime.UtcNow.AddHours(2));
            Assert.True((await source.SaveAsync(new ModConfigSaveRequest("carryon.json", "{}", saved.Content!.ChangedUtc))).Changed);

            var reset = await source.ResetAsync("carryon.json");
            Assert.False(reset.Exists);
            Assert.False(File.Exists(server.Local("/ModConfig/carryon.json")));

            var back = await source.UndoAsync("carryon.json");
            Assert.Equal("{ \"Range\": 5 }", back.Text);
            Assert.Equal("{ \"Range\": 5 }", File.ReadAllText(server.Local("/ModConfig/carryon.json")));
            Assert.Equal(1, back.Versions); // осталась версия до первой записи

            back = await source.UndoAsync("carryon.json");
            Assert.Equal("{ \"Range\": 3 }", File.ReadAllText(server.Local("/ModConfig/carryon.json")));
        }
        finally
        {
            HostedSessions.Forget(profile.Id);
            foreach (var dir in new[] { ModInfoCache.DirFor(profile.Id), HostedJournal.DirFor(profile.Id), Path.Combine(AppPaths.ModConfigBackups, profile.Id) })
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            if (File.Exists(ModHistory.FileFor(profile.Id))) File.Delete(ModHistory.FileFor(profile.Id));
        }
    }
}

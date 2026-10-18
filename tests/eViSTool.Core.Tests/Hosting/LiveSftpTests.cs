using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Hosting;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// Полный цикл на настоящем SFTP-сервере, как <see cref="LiveFtpTests"/>: ключ сервера (незнакомый — отказ с отпечатком,
/// чужой «доверенный» — отказ «ключ сменился»), проверка подключения, чтение модов, установка, обновление, выключение и
/// включение, удаление. Раскладка — «корень = папка сервера», как у G-Portal. Запускается только вручную, когда задан сервер:
///   EVISTOOL_LIVE_SFTP=хост:порт:логин и EVISTOOL_LIVE_SFTP_PASSWORD=пароль или EVISTOOL_LIVE_SFTP_KEY=путь к ключу
///   (необязательно: EVISTOOL_LIVE_SFTP_KEY_PASSPHRASE — пароль ключа, EVISTOOL_LIVE_SFTP_HOSTKEY — ожидаемый отпечаток
///   «SHA256:…», EVISTOOL_LIVE_SFTP_ROOT — папка сервера, если не «/»).
/// Без отпечатка ключ сервера принимается при первом подключении (как кнопкой «Доверять этому ключу») и печатается.
/// Ставит на сервер мод-пустышку evistoollivetest (только modinfo.json) и в конце удаляет его. Сервер на время теста
/// должен быть остановлен.
/// </summary>
public sealed class LiveSftpTests(ITestOutputHelper output)
{
    private const string ModId = "evistoollivetest";

    [Fact]
    public async Task FullCycle_OnARealServer()
    {
        var spec = Environment.GetEnvironmentVariable("EVISTOOL_LIVE_SFTP");
        var password = Environment.GetEnvironmentVariable("EVISTOOL_LIVE_SFTP_PASSWORD");
        var key = Environment.GetEnvironmentVariable("EVISTOOL_LIVE_SFTP_KEY");
        if (string.IsNullOrEmpty(spec) || password is null && string.IsNullOrEmpty(key))
        {
            output.WriteLine("EVISTOOL_LIVE_SFTP не задан — живой тест пропущен.");
            return;
        }
        var parts = spec.Split(':', 3);
        HostedTransports.Register();
        var passphrase = Environment.GetEnvironmentVariable("EVISTOOL_LIVE_SFTP_KEY_PASSPHRASE");
        var connection = new HostedConnection
        {
            Protocol = HostedProtocol.Sftp, Host = parts[0], Port = int.Parse(parts[1]), User = parts[2],
            Password = string.IsNullOrEmpty(password) ? null : HostedSecret.Protect(password),
            KeyFile = string.IsNullOrEmpty(key) ? null : key,
            KeyPassphrase = string.IsNullOrEmpty(passphrase) ? null : HostedSecret.Protect(passphrase),
            Root = Environment.GetEnvironmentVariable("EVISTOOL_LIVE_SFTP_ROOT") is { Length: > 0 } root ? root : "/",
        };
        (password, passphrase) = (null, null);
        var clock = Stopwatch.StartNew();
        void Log(string text) => output.WriteLine($"[{clock.Elapsed.TotalSeconds,6:0.00} с] {text}");

        // ключ сервера: незнакомый — отказ с отпечатком, который окно предложит человеку
        var unknown = await Assert.ThrowsAsync<RemoteFilesException>(() =>
            HostedProbe.CheckAsync(HostedEndpoint.From(connection)!, connection, CancellationToken.None));
        Assert.Equal(RemoteFailure.CertificateUntrusted, unknown.Kind);
        Assert.True(unknown.IsHostKey);
        Assert.False(unknown.HostKeyChanged);
        var hostKey = unknown.CertificateSha256!;
        Log($"ключ сервера: {unknown.CertificateSubject} {hostKey}");
        if (Environment.GetEnvironmentVariable("EVISTOOL_LIVE_SFTP_HOSTKEY") is { Length: > 0 } expected)
            Assert.True(SshNetFiles.SameFingerprint(expected, hostKey), $"ждали {expected}, сервер показал {hostKey}");

        // доверяли другому ключу — отказ «ключ сменился», дальше не идём
        connection.HostKeySha256 = "SHA256:" + new string('A', 43);
        var changed = await Assert.ThrowsAsync<RemoteFilesException>(() =>
            HostedProbe.CheckAsync(HostedEndpoint.From(connection)!, connection, CancellationToken.None));
        Assert.True(changed is { Kind: RemoteFailure.CertificateUntrusted, IsHostKey: true, HostKeyChanged: true });
        Log("чужой доверенный ключ — отказ «ключ сменился»");
        connection.HostKeySha256 = hostKey;

        // «Проверить подключение», как в окне
        var endpoint = HostedEndpoint.From(connection)!;
        var check = await HostedProbe.CheckAsync(endpoint, connection, CancellationToken.None);
        Log($"проверка: конфиг {check.Layout.ConfigPath}, данные {check.Layout.DataDir}, игра {check.Layout.GameDir}, " +
            $"папки модов [{string.Join(", ", check.Layout.ModDirs)}], ставить в {check.Layout.InstallDir}, " +
            $"версия {check.GameVersion} ({check.VersionSource}), модов {check.ModCount}, сервер «{check.Features.Banner}»");
        foreach (var w in check.Layout.Warnings) Log("  предупреждение: " + w);
        Assert.True(check.Features.Encrypted);
        Assert.True(check.Features.RangeReads);
        connection = HostedProbe.Pin(connection, check.Layout);

        var profile = new GameProfile
        {
            Id = "live-" + Guid.NewGuid().ToString("N")[..8], Name = "Live SFTP", Kind = ProfileKind.Server,
            RemoteCode = GameProfile.HostedMarker, Hosted = connection,
        };
        var target = ModTarget.For(profile) ?? throw new InvalidOperationException("цель не собралась");
        var configBefore = check.Layout.ConfigPath is { } cp ? await ReadAsync(endpoint, cp) : null;

        try
        {
            var (resolved, mods) = await ModTargets.ScanAsync(target);
            Log($"скан 1: {mods.Count} модов, игра {resolved.GameVersion}");
            foreach (var m in mods) Log($"  {m.FileName}: {m.Info?.ModId} {m.Info?.Version}{(m.IsDisabled ? " (выключен)" : "")}{(m.Error is null ? "" : " — " + m.Error)}");
            var (_, again) = await ModTargets.ScanAsync(target);
            Log($"скан 2 (тёплый кэш): {again.Count} модов");

            var v1 = MakeMod("1.0.0");
            var installed = await ModTargets.InstallAsync(target, v1);
            Log($"установка: {installed?.Text}");
            Assert.Equal("1.0.0", Find(await ModTargets.ScanAsync(target))?.Info?.Version);

            var v2 = MakeMod("1.0.1");
            installed = await ModTargets.InstallAsync(target, v2);
            Log($"обновление: {installed?.Text}");
            var (_, afterUpdate) = await ModTargets.ScanAsync(target);
            Assert.Single(afterUpdate, m => m.Info?.ModId == ModId);
            Assert.Equal("1.0.1", Find((resolved, afterUpdate))?.Info?.Version);

            await ModTargets.SetEnabledAsync(target, Find((resolved, afterUpdate))!, enabled: false);
            Assert.True(Find(await ModTargets.ScanAsync(target))?.IsDisabled);
            Log("выключен");
            if (check.Layout.ConfigPath is { } path && configBefore is not null)
            {
                // кроме DisabledMods в конфиге не поменялось ничего
                var after = JObject.Parse(await ReadAsync(endpoint, path));
                var before = JObject.Parse(configBefore);
                after.SelectToken("WorldConfig.DisabledMods")?.Parent?.Remove();
                before.SelectToken("WorldConfig.DisabledMods")?.Parent?.Remove();
                Assert.True(JToken.DeepEquals(before, after), "в serverconfig.json поменялось что-то кроме DisabledMods");
                Log("конфиг: кроме DisabledMods всё как было");
            }

            await ModTargets.SetEnabledAsync(target, Find(await ModTargets.ScanAsync(target))!, enabled: true);
            Assert.False(Find(await ModTargets.ScanAsync(target))?.IsDisabled);
            Log("включён");

            await ModTargets.DeleteAsync(target, Find(await ModTargets.ScanAsync(target))!, _ => { });
            Assert.Null(Find(await ModTargets.ScanAsync(target)));
            Log("удалён");
        }
        finally
        {
            // мод-пустышка не должен остаться на сервере, даже если тест упал
            try
            {
                if (Find(await ModTargets.ScanAsync(target)) is { } left)
                {
                    await ModTargets.DeleteAsync(target, left, _ => { });
                    Log("уборка: пустышка удалена");
                }
            }
            catch (Exception ex)
            {
                Log("уборка не удалась: " + ex.Message);
            }
            HostedSessions.Forget(profile.Id);
        }
    }

    private static LocalMod? Find((ResolvedProfile, IReadOnlyList<LocalMod> Mods) scan) =>
        scan.Mods.FirstOrDefault(m => m.Info?.ModId == ModId);

    private static async Task<string> ReadAsync(HostedEndpoint endpoint, string path)
    {
        await using var files = RemoteFilesFactory.Create(endpoint);
        await files.ConnectAsync(CancellationToken.None);
        return await files.ReadTextAsync(path, CancellationToken.None);
    }

    private static string MakeMod(string version)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-live-" + Guid.NewGuid().ToString("N"))).FullName;
        var zip = Path.Combine(dir, $"{ModId}_{version}.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("modinfo.json").Open(), new UTF8Encoding(false)))
            writer.Write($$"""{ "type": "content", "modid": "{{ModId}}", "name": "eViSTool live test", "version": "{{version}}", "side": "Server" }""");
        return zip;
    }
}

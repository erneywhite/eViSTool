using System.Diagnostics;
using System.Text;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server.Remote;
using Newtonsoft.Json;

namespace eViSTool.Core.Server;

/// <summary>Клиент агента: окно eViSTool управляет сервером через него.</summary>
public sealed class AgentClient : IDisposable
{
    private readonly HttpClient _http;

    public AgentEndpoint Endpoint { get; }

    public AgentClient(AgentEndpoint endpoint, string key) : this(endpoint, new Uri($"http://127.0.0.1:{endpoint.Port}/"), key, null) { }

    private AgentClient(AgentEndpoint endpoint, Uri baseAddress, string key, string? fingerprint)
    {
        Endpoint = endpoint;
        var handler = new HttpClientHandler();
        // удалённый агент — только тот, чей сертификат указан в коде подключения: подменить его по дороге не выйдет
        if (fingerprint is not null)
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert is not null && string.Equals(RemoteAccess.Fingerprint(cert), fingerprint, StringComparison.OrdinalIgnoreCase);
        _http = new HttpClient(handler)
        {
            BaseAddress = baseAddress,
            // таймаут — у каждого запроса свой (Timed): обычный ответ ждём 40 с (долгий опрос консоли — до 30 с),
            // а загрузка архива мода по медленной сети может идти минуты
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.Add(AgentProtocol.KeyHeader, key);
    }

    /// <summary>Агент на другой машине — по коду подключения (HTTPS, сертификат сверяется с отпечатком из кода).</summary>
    public static AgentClient ForRemote(ConnectionCode code) =>
        new(new AgentEndpoint(0, code.Port, default, ""), new UriBuilder(Uri.UriSchemeHttps, code.Host, code.Port).Uri, code.Key, code.Fingerprint);

    /// <summary>Подключиться к уже работающему агенту профиля (null — агента нет).</summary>
    public static AgentClient? TryConnect(string profileId, string? agentsDir = null)
    {
        var file = AgentProtocol.StateFile(profileId, agentsDir);
        if (!File.Exists(file)) return null;
        try
        {
            var endpoint = JsonConvert.DeserializeObject<AgentEndpoint>(File.ReadAllText(file));
            if (endpoint is null || !IsAlive(endpoint.Pid)) return null;
            return new AgentClient(endpoint, AgentProtocol.GetOrCreateKey(profileId, agentsDir));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("eViSTool.Agent", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public Task<AgentStatus> StatusAsync(CancellationToken ct = default) => Get<AgentStatus>("status", ct);

    /// <summary>Строки консоли новее since; если их нет — агент подождёт до wait секунд.</summary>
    public Task<List<ConsoleLine>> ConsoleAsync(long since, int waitSeconds = 25, CancellationToken ct = default) =>
        Get<List<ConsoleLine>>($"console?since={since}&wait={waitSeconds}", ct);

    public Task<AgentStatus> StartAsync(CancellationToken ct = default) => Post("start", null, ct);
    public Task<AgentStatus> StopAsync(CancellationToken ct = default) => Post("stop", null, ct);
    public Task<AgentStatus> RestartAsync(CancellationToken ct = default) => Post("restart", null, ct);
    public Task<AgentStatus> KillAsync(CancellationToken ct = default) => Post("kill", null, ct);
    public Task<AgentStatus> ShutdownAsync(CancellationToken ct = default) => Post("shutdown", null, ct);
    public Task<AgentStatus> CommandAsync(string text, CancellationToken ct = default) => Post("command", new CommandRequest(text), ct);

    /// <summary>Сделать копию мира сейчас (сервер должен работать).</summary>
    public Task<AgentStatus> BackupAsync(CancellationToken ct = default) => Post("backup", null, ct);

    // ---- расписание и резервные копии (для удалённого сервера: окно не видит его файлов)

    public Task<ServerAutomation> GetAutomationAsync(CancellationToken ct = default) => Get<ServerAutomation>("automation", ct);

    public async Task SaveAutomationAsync(ServerAutomation settings, CancellationToken ct = default)
    {
        using var content = new StringContent(JsonConvert.SerializeObject(settings), Encoding.UTF8, "application/json");
        using var cts = Timed(ct);
        using var resp = await _http.PutAsync("automation", content, cts.Token).ConfigureAwait(false);
        await Read<AgentStatus>(resp, ct).ConfigureAwait(false);
    }

    /// <summary>Обновить eViSTool на компьютере агента до этой версии (работающий сервер он остановит и запустит снова).</summary>
    public Task<AgentStatus> SelfUpdateAsync(string version, CancellationToken ct = default) =>
        Post<AgentStatus>("self-update", new SelfUpdateRequest(version), ct);

    // ---- оповещения сервера (вкладка «Оповещения»): секреты каналов едут только сюда, обратно — без них

    public Task<Notifications.ServerNotifySettings> GetNotifyAsync(CancellationToken ct = default) =>
        Get<Notifications.ServerNotifySettings>("notify", ct);

    public async Task SaveNotifyAsync(Notifications.ServerNotifyUpload settings, CancellationToken ct = default)
    {
        using var content = new StringContent(JsonConvert.SerializeObject(settings), Encoding.UTF8, "application/json");
        using var cts = Timed(ct);
        using var resp = await _http.PutAsync("notify", content, cts.Token).ConfigureAwait(false);
        await Read<AgentStatus>(resp, ct).ConfigureAwait(false);
    }

    /// <summary>Проверочное с сервера во все его каналы; ответ — ошибки по каналам (пусто — всё ушло).</summary>
    public async Task<IReadOnlyList<string>> TestNotifyAsync(CancellationToken ct = default) =>
        await Post<List<string>>("notify/test", null, ct).ConfigureAwait(false);

    // ---- объявления по расписанию (вкладка «Объявления»)

    public Task<ServerAnnouncements> GetAnnouncementsAsync(CancellationToken ct = default) => Get<ServerAnnouncements>("announcements", ct);

    public async Task SaveAnnouncementsAsync(ServerAnnouncements settings, CancellationToken ct = default)
    {
        using var content = new StringContent(JsonConvert.SerializeObject(settings), Encoding.UTF8, "application/json");
        using var cts = Timed(ct);
        using var resp = await _http.PutAsync("announcements", content, cts.Token).ConfigureAwait(false);
        await Read<AgentStatus>(resp, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BackupEntry>> BackupsAsync(CancellationToken ct = default) => await Get<List<BackupEntry>>("backups", ct).ConfigureAwait(false);
    public Task<BackupEntry> CopyWorldAsync(CancellationToken ct = default) => Post<BackupEntry>("backups/copy", null, ct);
    public Task<RestoreResult> RestoreAsync(string name, CancellationToken ct = default) => Post<RestoreResult>("backups/restore", new BackupNameRequest(name), ct);
    public Task DeleteBackupAsync(string name, CancellationToken ct = default) => Post<AgentStatus>("backups/delete", new BackupNameRequest(name), ct);
    public async Task<string?> CheckBackupDirAsync(string? dir, CancellationToken ct = default) =>
        (await Post<BackupDirCheck>("backups/check-dir", new BackupNameRequest(dir ?? ""), ct).ConfigureAwait(false)).Error;

    // ---- serverconfig.json удалённого сервера

    public Task<RemoteConfigFile> GetConfigAsync(CancellationToken ct = default) => Get<RemoteConfigFile>("config", ct);

    public async Task<ConfigSaveResult> SaveConfigAsync(ConfigSaveRequest request, CancellationToken ct = default)
    {
        using var content = new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json");
        using var cts = Timed(ct);
        using var resp = await _http.PutAsync("config", content, cts.Token).ConfigureAwait(false);
        return await Read<ConfigSaveResult>(resp, ct).ConfigureAwait(false);
    }

    /// <summary>Конфига ещё нет — агент попросит сервер записать конфиг по умолчанию.</summary>
    public Task<RemoteConfigFile> GenerateConfigAsync(CancellationToken ct = default) => Post<RemoteConfigFile>("config/generate", null, ct);

    // ---- моды сервера (для окна на другой машине)

    public Task<RemoteModList> ModsAsync(CancellationToken ct = default) => Get<RemoteModList>("mods", ct);

    /// <summary>Команды сервера из его /help — для подсказок в консоли.</summary>
    public Task<List<ServerCommand>> CommandsAsync(CancellationToken ct = default) => Get<List<ServerCommand>>("commands", ct);
    public Task SetModEnabledAsync(string path, bool enabled, CancellationToken ct = default) =>
        Post<AgentStatus>("mods/toggle", new ModToggleRequest(path, enabled), ct);
    public Task DeleteModAsync(string path, CancellationToken ct = default) => Post<AgentStatus>("mods/delete", new ModPathRequest(path), ct);

    // ---- игроки сервера (вкладка «Игроки»): чтение и правка файлов у остановленного; у запущенного — команды (CommandAsync)

    public Task<ServerPlayersView> PlayersAsync(CancellationToken ct = default) => Get<ServerPlayersView>("players", ct);
    public Task<AgentStatus> EditPlayersAsync(PlayerFileEdit edit, CancellationToken ct = default) => Post<AgentStatus>("players/edit", edit, ct);

    // ---- настройки модов сервера (ModConfig) — для окна на другой машине

    public Task<IReadOnlyList<ModConfigEntry>> ModConfigsAsync(CancellationToken ct = default) =>
        Get<IReadOnlyList<ModConfigEntry>>("modconfig", ct);
    public Task<ModConfigContent> ReadModConfigAsync(string path, CancellationToken ct = default) =>
        Post<ModConfigContent>("modconfig/read", new ModConfigPathRequest(path), ct);
    public Task<ModConfigSaveResult> SaveModConfigAsync(ModConfigSaveRequest request, CancellationToken ct = default) =>
        Post<ModConfigSaveResult>("modconfig/save", request, ct);
    public Task<ModConfigContent> UndoModConfigAsync(string path, CancellationToken ct = default) =>
        Post<ModConfigContent>("modconfig/undo", new ModConfigPathRequest(path), ct);
    public Task<ModConfigContent> ResetModConfigAsync(string path, CancellationToken ct = default) =>
        Post<ModConfigContent>("modconfig/reset", new ModConfigPathRequest(path), ct);

    /// <summary>Отправить архив мода агенту — он поставит его в папку модов сервера (как установка на этой машине).</summary>
    public async Task<ModInstallResult> InstallModAsync(string zipPath, CancellationToken ct = default)
    {
        await using var file = File.OpenRead(zipPath);
        using var content = new StreamContent(file);
        content.Headers.Add(ModFileHeader, Uri.EscapeDataString(Path.GetFileName(zipPath)));
        using var cts = Timed(ct, TimeSpan.FromMinutes(15));
        using var resp = await _http.PostAsync("mods/install", content, cts.Token).ConfigureAwait(false);
        return await Read<ModInstallResult>(resp, cts.Token).ConfigureAwait(false);
    }

    /// <summary>Имя файла присланного архива (имя важно: мод ляжет в папку под ним).</summary>
    public const string ModFileHeader = "X-eViSTool-File";

    private static CancellationTokenSource Timed(CancellationToken ct, TimeSpan? timeout = null)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(40));
        return cts;
    }

    private async Task<T> Get<T>(string path, CancellationToken ct)
    {
        using var cts = Timed(ct);
        using var resp = await _http.GetAsync(path, cts.Token).ConfigureAwait(false);
        return await Read<T>(resp, ct).ConfigureAwait(false);
    }

    private Task<AgentStatus> Post(string path, object? body, CancellationToken ct) => Post<AgentStatus>(path, body, ct);

    private async Task<T> Post<T>(string path, object? body, CancellationToken ct)
    {
        using var content = new StringContent(body is null ? "" : JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
        using var cts = Timed(ct);
        using var resp = await _http.PostAsync(path, content, cts.Token).ConfigureAwait(false);
        return await Read<T>(resp, ct).ConfigureAwait(false);
    }

    private static async Task<T> Read<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // агент отдаёт ошибки как problem+json: {"detail": "…"}
            var detail = TryDetail(text) ?? $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
            throw new InvalidOperationException(detail);
        }
        return JsonConvert.DeserializeObject<T>(text) ?? throw new InvalidOperationException("empty response");
    }

    private static string? TryDetail(string text)
    {
        try { return Newtonsoft.Json.Linq.JObject.Parse(text)["detail"]?.ToString(); }
        catch (JsonException) { return null; }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Запуск агента для серверного профиля (или подключение к уже работающему).</summary>
public static class AgentLauncher
{
    public static string ServerExe(GameProfile profile) =>
        Path.Combine(profile.GameDir ?? "", "VintagestoryServer.exe");

    /// <summary>Агент профиля: работающий — подключиться, нет — запустить (с --start сразу запустит и сервер).</summary>
    public static async Task<AgentClient> EnsureRunningAsync(GameProfile profile, bool startServer,
        string? agentExe = null, string? agentsDir = null, CancellationToken ct = default)
    {
        if (AgentClient.TryConnect(profile.Id, agentsDir) is { } existing)
        {
            try
            {
                var status = await existing.StatusAsync(ct).ConfigureAwait(false);
                if (!(AgentProtocol.IsOutdated(status) && status.State == ServerState.Stopped && status.RestartScheduledAt is null))
                {
                    if (startServer) await existing.StartAsync(ct).ConfigureAwait(false);
                    return existing;
                }
                // eViSTool обновили, а агент прежний; сервер стоит — меняем агента на новый (сервер при этом не трогаем)
                await ReplaceAsync(existing, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                existing.Dispose(); // файл остался от упавшего агента — запускаем новый
            }
        }

        if (string.IsNullOrWhiteSpace(profile.DataDir)) throw new InvalidOperationException(Loc.T("profile.dataNotFound"));
        var exe = ServerExe(profile);
        if (!File.Exists(exe)) throw new FileNotFoundException(Loc.T("srv.exeNotFound", exe), exe);

        agentExe ??= Path.Combine(AppContext.BaseDirectory, AgentProtocol.ExeName);
        if (!File.Exists(agentExe)) throw new FileNotFoundException(Loc.T("srv.agentMissing", agentExe), agentExe);

        var psi = new ProcessStartInfo(agentExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true, // своя скрытая консоль: в ней сервер пишет UTF-8 и получает Ctrl+C
            WorkingDirectory = Path.GetDirectoryName(agentExe)!,
        };
        psi.ArgumentList.Add("--profile"); psi.ArgumentList.Add(profile.Id);
        psi.ArgumentList.Add("--exe"); psi.ArgumentList.Add(exe);
        psi.ArgumentList.Add("--data"); psi.ArgumentList.Add(profile.DataDir);
        if (agentsDir is not null) { psi.ArgumentList.Add("--agents-dir"); psi.ArgumentList.Add(agentsDir); }
        if (startServer) psi.ArgumentList.Add("--start");
        // имя профиля — в имена резервных копий: по файлу видно, чей это мир
        psi.ArgumentList.Add("--backup-name"); psi.ArgumentList.Add(BackupStore.Slug(profile.Name));
        // сообщения агента (запуск, сторож, остановка) — на языке окна
        psi.ArgumentList.Add("--lang"); psi.ArgumentList.Add(Loc.Instance.Language);

        var stateFile = AgentProtocol.StateFile(profile.Id, agentsDir);
        try { File.Delete(stateFile); } catch (IOException) { }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException(Loc.T("srv.agentStartFailed"));
        var until = DateTime.Now.AddSeconds(20);
        while (DateTime.Now < until)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException(Loc.T("srv.agentExited", process.ExitCode));
            if (AgentClient.TryConnect(profile.Id, agentsDir) is { } client && client.Endpoint.Pid == process.Id)
                return client;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        throw new TimeoutException(Loc.T("srv.agentTimeout"));
    }

    /// <summary>
    /// Перед обновлением программы: остановить агентов, у которых сервер не работает, — иначе они продолжили бы жить
    /// со старым файлом и не знали бы новых функций. Агентов с работающим сервером не трогаем: сервер важнее,
    /// такой агент заменится после остановки сервера.
    /// </summary>
    public static async Task StopIdleAgentsAsync(IEnumerable<GameProfile> profiles, CancellationToken ct = default)
    {
        foreach (var profile in profiles.Where(p => p.Kind == ProfileKind.Server && !p.IsRemote))
        {
            if (AgentClient.TryConnect(profile.Id) is not { } client) continue;
            try
            {
                var status = await client.StatusAsync(ct).ConfigureAwait(false);
                if (status.State == ServerState.Stopped && status.RestartScheduledAt is null)
                {
                    await ReplaceAsync(client, ct).ConfigureAwait(false);
                    continue;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException) { }
            client.Dispose();
        }
    }

    /// <summary>Попросить агента выйти и дождаться, пока он действительно завершится.</summary>
    public static async Task ReplaceAsync(AgentClient existing, CancellationToken ct = default)
    {
        var pid = existing.Endpoint.Pid;
        try { await existing.ShutdownAsync(ct).ConfigureAwait(false); }
        catch (HttpRequestException) { /* уже выходит */ }
        existing.Dispose();
        try
        {
            using var p = Process.GetProcessById(pid);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await p.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException) { }
    }
}

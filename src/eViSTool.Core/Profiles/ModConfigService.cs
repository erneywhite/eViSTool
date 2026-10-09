using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.Core.Profiles;

/// <summary>Конфиг в списке: путь внутри ModConfig, как редактировать, чей, размер и время изменения.</summary>
public sealed record ModConfigEntry(string RelativePath, ModConfigKind Kind, string? ModId, string? ModName, long Size, DateTime ChangedUtc)
{
    public string FileName => Path.GetFileName(RelativePath);
}

/// <summary>Текст конфига; Exists = false — файла сейчас нет (сброшен или ещё не создан). Versions — сколько шагов назад есть.</summary>
public sealed record ModConfigContent(string RelativePath, string Text, bool Exists, int Versions, DateTime? ChangedUtc);

/// <summary>
/// Записать конфиг. <see cref="ExpectedChangedUtc"/> — время файла, когда его открыли: если с тех пор файл поменяли
/// (другое окно, сам мод), запись не делается — <see cref="ModConfigSaveResult.Changed"/>. null — записать в любом случае.
/// </summary>
public sealed record ModConfigSaveRequest(string Path, string Text, DateTime? ExpectedChangedUtc);

public sealed record ModConfigPathRequest(string Path);

/// <summary>Итог записи: новый текст и число версий — или Changed: файл поменяли с момента открытия, ничего не записано.</summary>
public sealed record ModConfigSaveResult(ModConfigContent? Content, bool Changed);

/// <summary>
/// Конфиги модов одного профиля по путям внутри ModConfig: список, чтение, запись с резервной копией, шаг назад, сброс.
/// Им пользуются и окно (свои профили), и агент (для окна на другой машине) — резервные копии лежат там, где файлы,
/// так что «Вернуть предыдущую версию» работает из любого окна. Путь за пределы ModConfig не пропускается.
/// </summary>
public sealed class ModConfigService(string dataDir, ModConfigBackups backups, Func<IReadOnlyList<LocalMod>> mods)
{
    public static ModConfigService ForProfile(GameProfile profile)
    {
        var data = string.IsNullOrWhiteSpace(profile.DataDir) ? Game.GameInstall.DefaultDataDir : profile.DataDir;
        return new ModConfigService(data, ModConfigBackups.ForProfile(profile),
            () => ModUpdateService.ScanLocal(ProfileResolver.Resolve(profile)));
    }

    public string Folder => ModConfigs.DirFor(dataDir);

    public IReadOnlyList<ModConfigEntry> List() =>
        [.. ModConfigs.List(dataDir, mods()).Select(f => new ModConfigEntry(f.RelativePath, f.Kind, f.ModId, f.ModName, f.Size, f.ChangedUtc))];

    /// <summary>
    /// Полный путь файла внутри ModConfig. Выход наружу («..», абсолютный путь, диск) и не-текстовые файлы — отказ.
    /// Путь присылает и окно с другой машины: на агенте под Linux «..\x» и «C:/x» отсекаются так же, как на Windows.
    /// </summary>
    public string FullPath(string relativePath)
    {
        var full = PathRules.Inside(Folder, relativePath);
        if (full is null || ModConfigs.KindOf(full) is null)
            throw new InvalidOperationException(Loc.T("mcfg.badPath", relativePath));
        return full;
    }

    private ModConfigFile File(string relativePath)
    {
        var full = FullPath(relativePath);
        // по этому пути хранятся резервные копии — берём его от проверенного полного: «a/../b.json» → «b.json»
        var rel = Path.GetRelativePath(Folder, full).Replace('\\', '/');
        return new ModConfigFile(full, rel, ModConfigs.KindOf(full)!.Value, null, null, 0, DateTime.MinValue);
    }

    public ModConfigContent Read(string relativePath)
    {
        var f = File(relativePath);
        var exists = System.IO.File.Exists(f.Path);
        return new ModConfigContent(f.RelativePath, exists ? ModConfigs.Read(f.Path) : "", exists,
            backups.Versions(f.RelativePath).Count, exists ? System.IO.File.GetLastWriteTimeUtc(f.Path) : null);
    }

    public ModConfigSaveResult Save(ModConfigSaveRequest request)
    {
        var f = File(request.Path);
        DateTime? now = System.IO.File.Exists(f.Path) ? System.IO.File.GetLastWriteTimeUtc(f.Path) : null;
        if (request.ExpectedChangedUtc is { } expected && now != expected) return new ModConfigSaveResult(null, Changed: true);
        ModConfigs.Save(f, request.Text, backups);
        return new ModConfigSaveResult(Read(request.Path), Changed: false);
    }

    /// <summary>Шаг назад; возвращать нечего — содержимое как есть.</summary>
    public ModConfigContent Undo(string relativePath)
    {
        var f = File(relativePath);
        backups.Undo(f.Path, f.RelativePath);
        return Read(relativePath);
    }

    public ModConfigContent Reset(string relativePath)
    {
        ModConfigs.Reset(File(relativePath), backups);
        return Read(relativePath);
    }
}

/// <summary>Откуда вкладка «Настройки модов» берёт конфиги: файлы этой машины или агент сервера на другой.</summary>
public interface IModConfigSource
{
    /// <summary>Путь на этой машине (кнопка «Показать файл»); у удалённого сервера — null.</summary>
    string? LocalPath(string relativePath);
    string? LocalFolder { get; }
    Task<IReadOnlyList<ModConfigEntry>> ListAsync(CancellationToken ct = default);
    Task<ModConfigContent> ReadAsync(string relativePath, CancellationToken ct = default);
    Task<ModConfigSaveResult> SaveAsync(ModConfigSaveRequest request, CancellationToken ct = default);
    Task<ModConfigContent> UndoAsync(string relativePath, CancellationToken ct = default);
    Task<ModConfigContent> ResetAsync(string relativePath, CancellationToken ct = default);
}

/// <summary>Свой профиль: файлы на этой машине (работа с диском — в фоне).</summary>
public sealed class LocalModConfigSource(ModConfigService service) : IModConfigSource
{
    public string? LocalPath(string relativePath) => service.FullPath(relativePath);
    public string? LocalFolder => service.Folder;
    public Task<IReadOnlyList<ModConfigEntry>> ListAsync(CancellationToken ct = default) => Task.Run(service.List, ct);
    public Task<ModConfigContent> ReadAsync(string relativePath, CancellationToken ct = default) => Task.Run(() => service.Read(relativePath), ct);
    public Task<ModConfigSaveResult> SaveAsync(ModConfigSaveRequest request, CancellationToken ct = default) => Task.Run(() => service.Save(request), ct);
    public Task<ModConfigContent> UndoAsync(string relativePath, CancellationToken ct = default) => Task.Run(() => service.Undo(relativePath), ct);
    public Task<ModConfigContent> ResetAsync(string relativePath, CancellationToken ct = default) => Task.Run(() => service.Reset(relativePath), ct);
}

/// <summary>Сервер на другой машине: те же действия через его агента (по коду подключения).</summary>
public sealed class RemoteModConfigSource(ConnectionCode code) : IModConfigSource
{
    public string? LocalPath(string relativePath) => null;
    public string? LocalFolder => null;

    private async Task<T> With<T>(Func<AgentClient, Task<T>> call)
    {
        using var agent = AgentClient.ForRemote(code);
        return await call(agent).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ModConfigEntry>> ListAsync(CancellationToken ct = default) => With(a => a.ModConfigsAsync(ct));
    public Task<ModConfigContent> ReadAsync(string relativePath, CancellationToken ct = default) => With(a => a.ReadModConfigAsync(relativePath, ct));
    public Task<ModConfigSaveResult> SaveAsync(ModConfigSaveRequest request, CancellationToken ct = default) => With(a => a.SaveModConfigAsync(request, ct));
    public Task<ModConfigContent> UndoAsync(string relativePath, CancellationToken ct = default) => With(a => a.UndoModConfigAsync(relativePath, ct));
    public Task<ModConfigContent> ResetAsync(string relativePath, CancellationToken ct = default) => With(a => a.ResetModConfigAsync(relativePath, ct));
}

using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.Presentation.ViewModels.Mods;

/// <summary>
/// Моды сервера у хостера — по FTP: листинг папок модов плюс кэш modinfo (<see cref="HostedMods"/>). Пока вкладка
/// «Мои моды» открыта, раз в <see cref="PollInterval"/> сверяется отпечаток папок и конфига (1–2 листинга): моды
/// могли поменять в панели хостера или в другом окне. Свои операции опрос не перебивает: пока они идут, он молчит.
/// </summary>
internal sealed class HostedModSource : IModSource
{
    /// <summary>Как часто, пока вкладка открыта, сверять отпечаток набора модов на сервере.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>Скан моложе этого срока второй раз не делается (автопроверка сразу после чтения).</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(10);

    private readonly ModsViewModel _owner;
    private readonly MainViewModel _main;
    private readonly IUiTimer _poll = Ui.Timer(PollInterval);

    private ResolvedProfile? _resolved;
    private string? _stamp;
    private DateTime? _scannedAt;
    private int _generation;
    private CancellationTokenSource? _cts;
    private bool _loading;
    private bool _polling;

    /// <summary>Моды ещё не читались для этого профиля: прочитать, когда вкладку откроют.</summary>
    private bool _stale = true;

    /// <summary>Какие предупреждения скана уже показаны по профилям: повторять их при каждом чтении незачем.</summary>
    private readonly Dictionary<string, HashSet<string>> _shownWarnings = new(StringComparer.Ordinal);

    public HostedModSource(ModsViewModel owner, MainViewModel main)
    {
        _owner = owner;
        _main = main;
        _poll.Tick += (_, _) => _ = PollAsync();
    }

    /// <summary>Часы скана — тесты подменяют.</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    public ResolvedProfile? Resolved => _resolved;

    /// <summary>Моды прочитаны недавно — перечитывать незачем.</summary>
    public bool IsFresh => _scannedAt is { } at && Now() - at < FreshFor;

    /// <summary>Идёт чтение модов с сервера.</summary>
    public bool IsLoading => _loading;

    /// <summary>Сменили профиль или его настройки: прочитанное и отпечаток — не про новый сервер, чтение прежнего отменяется.</summary>
    public void Reset()
    {
        _cts?.Cancel();
        _generation++;
        _loading = false;
        _resolved = null;
        _stamp = null;
        _scannedAt = null;
        _stale = true;
    }

    /// <summary>Вкладка открыта — следим за сервером, закрыта — не ходим на него зря.</summary>
    public void SetActive(bool active)
    {
        if (!active)
        {
            _poll.Stop();
            return;
        }
        if (!_poll.IsEnabled) _poll.Start();
        if (!_owner.IsHosted || _loading) return;
        if (_stale || _resolved is null) _ = ReloadAsync();
        else _ = PollAsync(); // вкладку открыли после перерыва — сервер мог измениться
    }

    public Task ReloadAsync() => ReadAsync();

    /// <summary>Прочитать моды с сервера. Ответ — прочитали ли (нет связи, профиль сменили, отменили — false).</summary>
    private async Task<bool> ReadAsync()
    {
        if (_main.ActiveProfile is not { } profile || !profile.Model.IsHosted) return false;
        if (ModTarget.For(profile.Model) is not { IsHosted: true } target)
        {
            _owner.StatusText = ModTarget.UnreadableText(profile.Model);
            return false;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var generation = ++_generation;
        _loading = true;
        _stale = false;
        var first = _resolved is null;
        var lastProgress = Loc.T("hosted.reading");
        try
        {
            // отметка — до чтения, как у диска: поменяли файлы, пока читали, — следующий опрос это заметит
            string? stamp = null;
            try
            {
                stamp = await ModTargets.StampAsync(target, cts.Token);
            }
            catch (Exception ex) when (IsExpected(ex) && ex is not OperationCanceledException)
            {
                // не вышло — об этом скажет само чтение
            }

            var hosted = new HostedMods(target);
            var progress = new Progress<string>(s =>
            {
                if (generation != _generation || !_loading) return;
                lastProgress = s;
                _owner.StatusText = s;
            });
            var (resolved, mods) = await hosted.ScanAsync(progress, cts.Token);
            if (generation != _generation || _main.ActiveProfile != profile) return false; // пока читали, профиль сменили или перечитали

            _resolved = resolved;
            _stamp = stamp;
            _scannedAt = Now();
            _owner.Locals = mods;
            profile.HostedGameVersion = resolved.GameVersion?.ToString();
            profile.HostedGameVersionSource = SourceText(hosted.VersionSource);
            if (_owner.StatusText == lastProgress) _owner.StatusText = Loc.T("mods.readFromServer");
            _owner.NotifyProfileRead();
            _owner.Rebuild();
            _owner.NotifyLocalModsChanged();
            ShowWarnings(profile.Model.Id, resolved.Warnings);
            if (first) _owner.AutoCheck();
            return true;
        }
        catch (OperationCanceledException)
        {
            // перечитывают заново или сменили профиль — итог придёт от нового чтения
            return false;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (generation == _generation) _owner.StatusText = Loc.T("mods.readFailed", ModTargets.Describe(target, ex));
            return false;
        }
        finally
        {
            if (generation == _generation) _loading = false;
            if (_cts == cts) _cts = null;
            cts.Dispose();
        }
    }

    /// <summary>
    /// Опрос: отпечаток папок модов и конфига поменялся — моды изменили со стороны, перечитываем. Первое чтение не
    /// удалось (не было связи) — пробуем прочитать снова, без сверки отпечатка.
    /// </summary>
    internal async Task PollAsync()
    {
        if (_polling || !IsQuiet() || _main.ActiveProfile is not { } profile || ModTarget.For(profile.Model) is not { IsHosted: true } target) return;
        _polling = true;
        var generation = _generation;
        try
        {
            if (_resolved is null)
            {
                await ReadAsync(); // не вышло — ошибка снова в статусе, следующий опрос попробует ещё
                return;
            }
            var stamp = await ModTargets.StampAsync(target);
            // пока ждали сервер, началась своя операция или чтение, или профиль сменили — это уже не «со стороны»
            if (generation != _generation || !IsQuiet() || _main.ActiveProfile != profile || stamp == _stamp) return;
            // перечитать не удалось — в статусе остаётся ошибка чтения: список устарел, и это надо видеть
            if (await ReadAsync() && _main.ActiveProfile == profile) _owner.StatusText = Loc.T("mods.changedOutside");
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // нет связи — не тревожим: следующий опрос попробует снова
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>Своих операций нет (откат с его удалениями — тоже своя) — можно смотреть, не изменили ли моды со стороны.</summary>
    private bool IsQuiet() =>
        _owner.IsHosted && !_stale && !_loading && !_owner.IsBusy
        && !_owner.ToggleEnabledCommand.IsRunning && !_owner.DeleteCommand.IsRunning
        && !_owner.RollbackSessionCommand.IsRunning && !_owner.RevertChangeCommand.IsRunning;

    /// <summary>
    /// Файлы настроек по модам — из списка, который «Настройки модов» уже прочитали с этого сервера: ради ссылки в
    /// карточке на сервер не ходим. Раздел в этом окне ещё не открывали — ссылки нет.
    /// </summary>
    public Dictionary<string, int> ConfigCounts(IReadOnlyList<LocalMod> mods)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (_resolved?.Profile is not { } profile || HostedModConfigSource.LastListed(profile.Id) is not { } files) return counts;
        foreach (var modId in files.Select(f => ModConfigs.MatchMod(f, mods)?.ModId).OfType<string>())
            counts[modId] = counts.GetValueOrDefault(modId) + 1;
        return counts;
    }

    /// <summary>
    /// Мод только для чтения: мод самого хостера (папка gportal_mods) или распакованная папка — по FTP их можно
    /// только показывать. Ответ — текст причины; null — менять можно.
    /// </summary>
    public static string? ReadOnlyReason(LocalMod mod)
    {
        if (HostedLayoutProbe.IsHosterDir(RemotePath.Parent(mod.Path))) return Loc.T("hosted.hosterMod");
        // shortcut: папку-мод узнаём по отсутствию расширения архива или кода; переделать, если ядро начнёт отдавать признак папки
        var ext = Path.GetExtension(mod.Path);
        return ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            ? null
            : Loc.T("hosted.folderModReadOnly");
    }

    /// <summary>Предупреждения скана (довели прерванную установку, нет конфига…) — окном, но каждое по разу, пока не пропадёт.</summary>
    private void ShowWarnings(string profileId, IReadOnlyList<string> warnings)
    {
        var shown = _shownWarnings.TryGetValue(profileId, out var set) ? set : _shownWarnings[profileId] = [];
        var fresh = warnings.Where(w => !shown.Contains(w)).ToList();
        shown.IntersectWith(warnings); // пропавшее предупреждение при возвращении покажется снова
        shown.UnionWith(fresh);
        if (fresh.Count > 0) Ui.Warn(string.Join("\n\n", fresh));
    }

    private static string SourceText(GameVersionSource? source) => source switch
    {
        GameVersionSource.Manual => Loc.T("hosted.versionSourceManual"),
        GameVersionSource.VersionFile => Loc.T("hosted.versionSourceFile"),
        GameVersionSource.LastLaunch => Loc.T("hosted.versionSourceLastLaunch"),
        GameVersionSource.ServerLog => Loc.T("hosted.versionSourceLog"),
        GameVersionSource.ServerDll => Loc.T("hosted.versionSourceDll"),
        _ => "",
    };

    private static bool IsExpected(Exception ex) =>
        ex is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException;
}

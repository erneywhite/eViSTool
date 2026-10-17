using System.Net.Http;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels.Mods;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Чем кончилось выключение мода: текст для строки состояния окна; <see cref="Failed"/> — не вышло, кнопку вернуть.</summary>
public sealed record ModDisableResult(string Message, bool Failed);

/// <summary>
/// Выключить мод профиля из окна вылета или из «Ошибок модов» — так же, как это делает менеджер модов игры: у своего
/// профиля — в его файле настроек, у удалённого сервера — через агента (мод там знают по пути на машине сервера, читать
/// архив не нужно). Пока игра профиля открыта, выключение откладывается до её закрытия: при выходе она перезапишет свои
/// настройки, и список выключенных модов вместе с ними. У сервера у хостера — как в «Моих модах»: по логу остановлен —
/// сразу, иначе вопрос «останови сервер в панели хостера». После выключения моды активного профиля перечитываются.
/// </summary>
public sealed class ModDisabler
{
    private readonly Func<GameProfile, bool> _isGameRunning;
    private readonly Action<GameProfile, Func<Task>> _runAfterGameExit;
    private readonly Action<GameProfile> _reloadIfActive;
    private readonly Func<GameProfile, ModTarget?> _targetFor;
    private readonly Func<ModTarget, string, Task> _disable;
    private readonly Func<ModTarget, Task<bool>> _confirmHosted;

    /// <summary>
    /// Выключатель главного окна программы: игра профиля, отложенные до её выхода дела, «Мои моды». Вопрос «останови
    /// сервер в панели хостера» — тот же, что у «Моих модов», с той же памятью ответа «Да».
    /// </summary>
    public ModDisabler(MainViewModel main)
        : this(main.IsGameRunningFor, main.RunAfterGameExit, profile =>
        {
            if (main.ActiveProfile?.Model.Id == profile.Id) main.Mods.ReloadLocal();
        }, confirmHosted: target => main.Mods.ConfirmIfRunningAsync(target))
    {
    }

    /// <param name="isGameRunning">Игра этого профиля сейчас открыта.</param>
    /// <param name="runAfterGameExit">Сделать, когда игра профиля закроется (моды после этого перечитывает сама программа).</param>
    /// <param name="reloadIfActive">Перечитать моды, если это активный профиль.</param>
    /// <param name="targetFor">Куда ставятся моды профиля; null — выключать нечем (код удалённого сервера не прочитать
    /// на этой машине). По умолчанию — <see cref="ModTarget.For"/>; тесты подменяют.</param>
    /// <param name="disable">Выключить мод по пути у цели; по умолчанию — <see cref="DisableModAsync"/> (тесты подменяют).</param>
    /// <param name="confirmHosted">Можно ли менять моды сервера у хостера: проверка по логу и вопрос «останови сервер в
    /// панели хостера»; false — не выключаем. По умолчанию — свой <see cref="HostedStopReminder"/> (без общей памяти ответа).</param>
    public ModDisabler(Func<GameProfile, bool> isGameRunning, Action<GameProfile, Func<Task>> runAfterGameExit,
        Action<GameProfile> reloadIfActive, Func<GameProfile, ModTarget?>? targetFor = null, Func<ModTarget, string, Task>? disable = null,
        Func<ModTarget, Task<bool>>? confirmHosted = null)
    {
        _isGameRunning = isGameRunning;
        _runAfterGameExit = runAfterGameExit;
        _reloadIfActive = reloadIfActive;
        _targetFor = targetFor ?? ModTarget.For;
        _disable = disable ?? DisableModAsync;
        _confirmHosted = confirmHosted ?? new HostedStopReminder().ConfirmAsync;
    }

    /// <summary>
    /// Выключение мода для кнопки окна; null — выключать нечего: путь к моду не известен (мод не нашёлся среди
    /// установленных) или у профиля нет цели. Цель берётся сейчас, когда окно открывается, — правка профиля в
    /// настройках, пока окно открыто, её уже не сдвинет (как и у установки модов).
    /// </summary>
    /// <param name="modPath">Путь к моду: у своего профиля — на этой машине, у удалённого сервера — на машине сервера.</param>
    /// <param name="modName">Имя мода для текста в окне.</param>
    public Func<Task<ModDisableResult>>? For(GameProfile profile, string? modPath, string? modName)
    {
        if (modPath is null || _targetFor(profile) is not { } target) return null;
        return () => DisableAsync(profile, target, modPath, modName);
    }

    private async Task<ModDisableResult> DisableAsync(GameProfile profile, ModTarget target, string modPath, string? modName)
    {
        try
        {
            // игра профиля открыта (мир закрылся, а она нет): при выходе она перезапишет список выключенных модов —
            // выключаем после её закрытия. Игру программа отслеживает только у клиентского профиля.
            if (profile.Kind == ProfileKind.Client && _isGameRunning(profile))
            {
                _runAfterGameExit(profile, () => _disable(target, modPath));
                return new ModDisableResult(Loc.T("crash.disableAfterExit", modName), Failed: false);
            }
            // у хостера запущен ли сервер, видно только по логу: вылетевший панель хостера могла уже запустить снова, а
            // запущенный при остановке перезапишет serverconfig.json вместе с выключенным модом. Не остановлен — кнопку вернуть
            if (target.IsHosted && !await _confirmHosted(target))
                return new ModDisableResult(Loc.T("crash.disableNotStopped"), Failed: true);
            await _disable(target, modPath);
            _reloadIfActive(profile);
            return new ModDisableResult(Loc.T(profile.Kind == ProfileKind.Server ? "crash.disabledServer" : "crash.disabled", modName), Failed: false);
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            return new ModDisableResult(Loc.T("crash.disableFailed", ex.Message), Failed: true);
        }
    }

    /// <summary>
    /// Ошибка, о которой окно говорит словами, а не падает: файл занят или недоступен, настроек игры ещё нет, мод не
    /// прочитать, нет связи с агентом удалённого сервера.
    /// </summary>
    public static bool IsExpectedFailure(Exception ex) =>
        ex is IOException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException;

    /// <summary>
    /// Мод по пути у цели: у удалённого сервера и у хостера путь — не на этой машине, цели хватит пути (хостер найдёт мод
    /// в свежем скане); у своего профиля архив читается сейчас (нужен modid). Не прочитался — мод с ошибкой,
    /// и выключение скажет, что не так.
    /// </summary>
    public static LocalMod ModAt(ModTarget target, string path) =>
        target.IsLocal ? ModScanner.ReadZip(path) : new LocalMod(path, null, null);

    /// <summary>Выключить мод у цели (по умолчанию для <see cref="ModDisabler"/>).</summary>
    public static Task DisableModAsync(ModTarget target, string path) =>
        ModTargets.SetEnabledAsync(target, ModAt(target, path), enabled: false);
}

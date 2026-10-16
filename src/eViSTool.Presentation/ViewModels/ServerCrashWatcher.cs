using System.Diagnostics.CodeAnalysis;
using System.IO;
using eViSTool.Core.Diagnostics;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Падения серверов: раз в 15 секунд спрашиваем все серверные профили (свои — через их агентов, удалённые — по коду),
/// а не только активный: сервер упал, пока открыт клиент, — оповещение всё равно приходит. Заодно забираем «ошибки
/// модов» сервера — для «!» у профиля.
/// </summary>
/// <param name="profiles">Все профили окна (живой список: серверные выбираются при каждом опросе).</param>
/// <param name="errors">«Ошибки модов» сервера из его статуса.</param>
/// <param name="crashed">Новое падение сервера — показать окно.</param>
/// <param name="startedAt">Когда открыто окно: падения раньше этого задним числом не показываем. По умолчанию — сейчас.</param>
public sealed class ServerCrashWatcher(
    IEnumerable<ProfileViewModel> profiles,
    Action<GameProfile, ModErrorReport> errors,
    Action<GameProfile, ServerCrashInfo> crashed,
    DateTime? startedAt = null)
{
    private readonly IUiTimer _timer = Ui.Timer(TimeSpan.FromSeconds(15));
    private readonly Dictionary<string, string?> _seen = [];
    private readonly DateTime _startedAt = startedAt ?? DateTime.Now;
    private bool _subscribed;
    private bool _checking;

    /// <summary>
    /// Включить опрос по таймеру. Повторный вызов только запускает таймер: на Tick подписываемся один раз,
    /// иначе опросы шли бы по два за тик.
    /// </summary>
    public void Start()
    {
        if (!_subscribed)
        {
            _timer.Tick += (_, _) => _ = CheckAsync();
            _subscribed = true;
        }
        _timer.Start();
    }

    /// <summary>Один опрос всех серверных профилей; пока предыдущий не закончился, новый не начинается.</summary>
    public async Task CheckAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            foreach (var profile in profiles.Where(p => p.Model.Kind == ProfileKind.Server).Select(p => p.Model).ToList())
            {
                ServerCrashInfo? crash;
                ModErrorReport? modErrors;
                try
                {
                    using var client = profile.IsRemote
                        ? RemoteSecret.Unprotect(profile.RemoteCode) is { } code ? AgentClient.ForRemote(code) : null
                        : AgentClient.TryConnect(profile.Id);
                    if (client is null) continue; // агента нет — сервер не работает и не падал под нами
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    var status = await client.StatusAsync(cts.Token);
                    (crash, modErrors) = (status.LastCrash, status.ModErrors);
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException
                                               or IOException or Newtonsoft.Json.JsonException)
                {
                    continue; // нет связи — спросим в следующий раз
                }

                // «ошибки модов» сервера — в «!» у профиля (и сохраняются, как у игры: «скрыть» помнится)
                if (modErrors is not null) errors(profile, modErrors);

                if (Notice(profile.Id, crash)) crashed(profile, crash);
            }
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>
    /// Учесть последнее падение сервера из его статуса (запоминается до следующего опроса). true — падение новое и о
    /// нём надо сказать: не то же, что в прошлый раз, и не случилось до открытия окна (при первом опросе профиля).
    /// </summary>
    public bool Notice(string profileId, [NotNullWhen(true)] ServerCrashInfo? crash)
    {
        var first = !_seen.ContainsKey(profileId);
        var seen = _seen.GetValueOrDefault(profileId);
        _seen[profileId] = crash?.Id;
        if (crash is null || crash.Id == seen) return false;
        if (first && crash.At < _startedAt) return false; // упал ещё до запуска окна — не тревожим задним числом
        return true;
    }
}

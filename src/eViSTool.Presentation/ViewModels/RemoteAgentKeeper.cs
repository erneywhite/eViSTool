using System.IO;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Удалённый доступ работает, пока открыто окно (или пока работает сервер): раз в 30 секунд держим агентов таких
/// профилей запущенными. Закрыли окно и сервер не работает — агент через пару минут выйдет, в системе ничего не остаётся.
/// </summary>
/// <param name="profiles">Все профили окна (живой список: серверные выбираются при каждом проходе).</param>
public sealed class RemoteAgentKeeper(IEnumerable<ProfileViewModel> profiles)
{
    private readonly IUiTimer _timer = Ui.Timer(TimeSpan.FromSeconds(30));

    /// <summary>Включить проходы по таймеру.</summary>
    public void Start()
    {
        _timer.Tick += (_, _) => _ = KeepAsync();
        _timer.Start();
    }

    /// <summary>Один проход: у серверных профилей с включённым удалённым доступом агент должен работать.</summary>
    public async Task KeepAsync()
    {
        foreach (var profile in profiles.Where(p => p.Kind == ProfileKind.Server).Select(p => p.Model).ToList())
        {
            if (!RemoteAccess.Load(profile.Id).Enabled) continue;
            try
            {
                using var client = await AgentLauncher.EnsureRunningAsync(profile, startServer: false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException
                                           or System.Net.Http.HttpRequestException or System.ComponentModel.Win32Exception)
            {
                // папки игры нет и т. п. — вкладка «Удалённый доступ» покажет, что агент не запущен
            }
        }
    }
}

using eViSTool.Core.Localization;
using eViSTool.Core.Mods;

namespace eViSTool.Presentation.ViewModels.Mods;

/// <summary>
/// Вопрос «останови сервер в панели хостера» перед изменением модов. Запущенный сервер новые моды не увидит, а при
/// остановке может перезаписать serverconfig.json; запущен ли он, по FTP не узнать, поэтому спрашиваем человека.
/// «Да» помнится <see cref="Memory"/> для профиля (серия операций подряд не переспрашивается); флажок профиля
/// «Напоминать остановить сервер» выключает вопрос совсем.
/// </summary>
internal sealed class HostedStopReminder(Func<DateTime>? now = null)
{
    /// <summary>Сколько помнится ответ «Да».</summary>
    public static readonly TimeSpan Memory = TimeSpan.FromMinutes(10);

    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    private readonly Dictionary<string, DateTime> _until = new(StringComparer.Ordinal);

    /// <summary>true — можно менять моды: сервер остановлен (ответили «Да» сейчас или недавно) или вопрос выключен.</summary>
    /// <param name="question">Свой текст вопроса (перед правкой настроек модов); память ответа — общая.</param>
    public bool Confirm(ModTarget target, string? question = null)
    {
        if (target.Hosted?.Settings.SkipStopReminder == true) return true;
        if (_until.TryGetValue(target.ProfileId, out var until) && _now() < until) return true;
        if (!Ui.Confirm(question ?? Loc.T("hosted.stopAsk", target.Name), byDefault: Answer.No, warning: true)) return false;
        _until[target.ProfileId] = _now() + Memory;
        return true;
    }
}

using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.Presentation.ViewModels.Mods;

/// <summary>
/// Откуда вкладка «Мои моды» берёт моды открытого профиля: свой — с диска этой машины (<see cref="LocalModSource"/>),
/// удалённый сервер — у его агента по сети (<see cref="RemoteModSource"/>). Прочитанный список источник отдаёт вкладке,
/// и она перестраивает таблицу. Как замечать изменения со стороны, у каждого своё: у диска — опрос папок, у сервера —
/// отметка в статусе агента.
/// </summary>
internal interface IModSource
{
    /// <summary>Профиль с папками модов и версией игры; null — ещё не известен (агент не ответил, профиль не выбран).</summary>
    ResolvedProfile? Resolved { get; }

    /// <summary>Перечитать моды и дождаться: вкладка получает новый список и перестраивает таблицу.</summary>
    Task ReloadAsync();

    /// <summary>Сколько файлов настроек (ModConfig) у каждого мода: modid → число. Для ссылки «Настройки мода» в карточке.</summary>
    Dictionary<string, int> ConfigCounts(IReadOnlyList<LocalMod> mods);
}

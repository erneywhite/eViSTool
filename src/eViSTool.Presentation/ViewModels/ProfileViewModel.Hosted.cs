using System.ComponentModel;
using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

// Подсказка под полем «Версия игры сервера» в настройках профиля у хостера. Отдельным файлом, чтобы не трогать основной.
public sealed partial class ProfileViewModel
{
    /// <summary>
    /// «Оставь пустым, и версия определится сама: 1.22.7 (из serverconfig.json)». Пока чтения модов не было —
    /// «узнаю при чтении модов».
    /// </summary>
    public string HostedGameVersionHint
    {
        get
        {
            var found = HostedGameVersion is { Length: > 0 } v
                ? HostedGameVersionSource.Length > 0 ? $"{v} ({HostedGameVersionSource})" : v
                : Loc.T("profile.hostedVersion");
            return Loc.T("settings.hostedGameVersionHint", found);
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // версию и её источник ставит чтение модов (свойства-контракты); подсказка собрана из них обоих.
        // null и "" — «поменялось всё»
        if (e.PropertyName is nameof(HostedGameVersion) or nameof(HostedGameVersionSource) or null or "")
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HostedGameVersionHint)));
    }
}

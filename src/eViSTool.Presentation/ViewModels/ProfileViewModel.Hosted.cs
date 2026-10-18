using System.ComponentModel;
using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

// Подсказка под полем «Версия игры сервера» в настройках профиля у хостера и настройки копий мира у хостера.
// Отдельным файлом, чтобы не трогать основной.
public sealed partial class ProfileViewModel
{
    // ---------- копии мира у хостера (вкладка «Копии» раздела «Сервер»)
    // Сохраняются сразу и только сохраняются (_remembered): это не правка профиля — моды перечитывать незачем, а
    // соединение и кэш остаются как были.

    /// <summary>Скачивать копию мира по расписанию, пока окно открыто.</summary>
    public HostedBackupSchedule BackupSchedule
    {
        get => Model.Hosted?.BackupSchedule ?? HostedBackupSchedule.Off;
        set
        {
            if (Model.Hosted is not { } h || h.BackupSchedule == value) return;
            h.BackupSchedule = value;
            OnPropertyChanged();
            _remembered?.Invoke(this);
        }
    }

    /// <summary>Сколько последних своих копий хранить; 0 — не удалять старые.</summary>
    public int BackupKeep
    {
        get => Model.Hosted?.BackupKeep ?? 7;
        set
        {
            if (Model.Hosted is not { } h || h.BackupKeep == value) return;
            h.BackupKeep = value;
            OnPropertyChanged();
            _remembered?.Invoke(this);
        }
    }

    /// <summary>Своя папка для копий (полный путь, уже проверенный); null или пусто — папка программы.</summary>
    public string? BackupDir
    {
        get => Model.Hosted?.BackupDir;
        set
        {
            var dir = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (Model.Hosted is not { } h || h.BackupDir == dir) return;
            h.BackupDir = dir;
            OnPropertyChanged();
            _remembered?.Invoke(this);
        }
    }

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

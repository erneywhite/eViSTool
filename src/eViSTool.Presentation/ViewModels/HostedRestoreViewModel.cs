using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Окно «Восстановить мир на сервер» (сервер у хостера): какая копия (дата, размер, откуда скачана), что мир на сервере
/// заменится, а нынешний останется рядом с ним как bak, и галочка «и настройки модов, игроков и данные модов». Галочка
/// доступна, только если у копии есть архив с ними; по умолчанию она стоит: данные модов — часть мира. Копия чужая (другой
/// профиль, положена руками) — ещё и предупреждение. «Восстановить» — <see cref="Finished"/> с ответом.
/// </summary>
public sealed partial class HostedRestoreViewModel : ObservableObject
{
    public HostedRestoreViewModel(HostedWorldBackup backup, string serverName)
    {
        Heading = Loc.T("hostedrst.heading", serverName);
        CopyText = Loc.T("hostedrst.copy", Dates.Stamp(backup.Time), Sizes.Format(backup.Size + backup.ExtraSize), HostedBackupRowViewModel.OriginOf(backup));
        Foreign = backup.FromOtherProfile ? Loc.T("hostedrst.otherProfile") : !backup.IsOwn ? Loc.T("hostedrst.manual") : "";
        HasExtra = backup.HasExtra;
        _extras = backup.HasExtra;
        ExtrasHint = HasExtra ? Loc.T("hostedrst.extrasHint") : Loc.T("hostedrst.noExtras");
    }

    /// <summary>«Восстановить мир на сервер «…»?»</summary>
    public string Heading { get; }

    /// <summary>«Копия от 18.10.2026 09:00 · 90 МБ · мир с сервера».</summary>
    public string CopyText { get; }

    /// <summary>Копия не этого профиля: возможно, мир другого сервера. Пусто — своя.</summary>
    public string Foreign { get; }

    public bool HasForeign => Foreign.Length > 0;

    /// <summary>У копии есть архив с настройками и данными модов — галочку можно ставить.</summary>
    public bool HasExtra { get; }

    public string ExtrasHint { get; }

    /// <summary>Вернуть вместе с миром настройки модов, игроков и данные модов.</summary>
    [ObservableProperty] private bool _extras;

    /// <summary>Ответ окна; null — ещё не выбрали (или закрыли).</summary>
    public HostedRestoreChoice? Result { get; private set; }

    /// <summary>«Восстановить» нажали — окно закрывается с этим ответом.</summary>
    public event Action<HostedRestoreChoice>? Finished;

    [RelayCommand]
    private void Go()
    {
        var choice = new HostedRestoreChoice(Extras && HasExtra);
        Result = choice;
        Finished?.Invoke(choice);
    }
}

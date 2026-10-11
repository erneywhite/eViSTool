using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>Окно «Установить также в» (dialog.alsoinstall, dialog.alsoinstall.long).</summary>
internal static class AlsoInstallSnapshot
{
    private static ProfileViewModel Profile(string name, ProfileKind kind) => new(new GameProfile { Name = name, Kind = kind }, _ => { });

    /// <summary>Текущий клиент, связанный сервер (отмечен заранее) и клиент, куда мод поставить нельзя.</summary>
    [SnapshotDialog("alsoinstall")]
    private static void Open(MainViewModel main)
    {
        var willInstall = Loc.T("also.willInstall", Loc.T("also.game", "1.21.3"), "2.1.0");
        AlsoChoice[] others =
        [
            new(Profile("Сервер друзей", ProfileKind.Server), "server", willInstall, CanInstall: true, AlreadyThere: false, Linked: true),
            new(Profile("Сборка 1.19", ProfileKind.Client), "client", Loc.T("also.noRelease", Loc.T("also.game", "1.19.8"), "1.19.x"),
                CanInstall: false, AlreadyThere: false, Linked: false),
        ];
        AppWindows.Current.AskAlsoInstall(Loc.T("also.hint", "Primitive Survival"), "Основной", Loc.T("profile.kindClient"),
            Loc.T("also.thisProfile") + " · " + willInstall, others);
    }

    /// <summary>Длинные тексты: несколько файлов сразу, переносы в пояснении и в строках, недоступный сервер с ошибкой связи.</summary>
    [SnapshotDialog("alsoinstall.long")]
    private static void OpenLong(MainViewModel main)
    {
        var game = Loc.T("also.game", "1.21.3");
        var files = string.Join(", ", new[] { "Primitive Survival 3.8.2", "Carry On 1.8.4", "Expanded Foods 2.3.0", "Wildcraft Tree 1.3.11", "Rivers 1.1.5" });
        AlsoChoice[] others =
        [
            new(Profile("Сервер друзей на выходные", ProfileKind.Server), "server", Loc.T("also.files", game, files) + " " + Loc.T("also.filesSome", 2),
                CanInstall: true, AlreadyThere: false, Linked: true),
            new(Profile("Сервер в облаке", ProfileKind.Server), "remote", Loc.T("also.unavailable", "агент не отвечает: превышено время ожидания ответа от сервера"),
                CanInstall: false, AlreadyThere: false, Linked: true),
            new(Profile("Сборка 1.19", ProfileKind.Client), "client", Loc.T("also.allThere", Loc.T("also.game", "1.19.8")),
                CanInstall: true, AlreadyThere: true, Linked: false),
        ];
        AppWindows.Current.AskAlsoInstall(Loc.T("also.hintFiles", "Primitive Survival, Carry On, Expanded Foods, Wildcraft Tree, Rivers"),
            "Основной", Loc.T("profile.kindClient"), Loc.T("also.thisProfile") + " · " + Loc.T("also.game", "1.21.3"), others);
    }
}

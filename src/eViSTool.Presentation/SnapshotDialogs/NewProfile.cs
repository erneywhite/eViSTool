using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Окно «Новый профиль»: серверный (dialog.newprofile) и клиентский (dialog.newprofile.client). Название — как его
/// предлагает «Добавить профиль» в настройках.
/// </summary>
internal static class NewProfile
{
    [SnapshotDialog("newprofile")]
    private static void Server(MainViewModel main) =>
        AppWindows.Current.NewProfile(ProfileKind.Server, Loc.T("profile.newServer", main.Profiles.Count + 1));

    [SnapshotDialog("newprofile.client")]
    private static void Client(MainViewModel main) =>
        AppWindows.Current.NewProfile(ProfileKind.Client, Loc.T("profile.newClient", main.Profiles.Count + 1));
}

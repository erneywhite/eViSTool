using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>Свойства-контракты профиля «Сервер у хостера» в модели настроек: к ним привяжется разметка следующих пакетов.</summary>
public sealed class HostedProfileContractTests : IDisposable
{
    private readonly IAppWindows? _oldWindows = AppWindows.IsSet ? AppWindows.Current : null;

    public void Dispose() => AppWindows.Current = _oldWindows!;

    private static GameProfile Hosted() => new()
    {
        Name = "G-Portal", Kind = ProfileKind.Server, RemoteCode = GameProfile.HostedMarker,
        Hosted = new HostedConnection { Host = "198.51.100.7", Port = 58021, Tls = FtpTls.None, User = "gp_user", Password = HostedSecret.Protect("pw") },
    };

    [Fact]
    public void HostedProfile_ShowsKindTargetAndVersion_WithoutPassword()
    {
        var changes = 0;
        var vm = new ProfileViewModel(Hosted(), _ => changes++);

        Assert.True(vm.IsHosted);
        Assert.False(vm.IsLocal);
        Assert.False(vm.IsRemote);
        Assert.Equal(Loc.T("profile.kindHosted"), vm.KindText);
        Assert.Equal("ftp://gp_user@198.51.100.7:58021 · " + Loc.T("hosted.noEncryption"), vm.HostedTarget);
        Assert.DoesNotContain(vm.Model.Hosted!.Password!, vm.HostedTarget);
        Assert.Equal(Loc.T("profile.hostedVersion"), vm.GameVersionText);
        Assert.True(vm.Resolved.OffMachine);

        vm.HostedGameVersion = "1.22.7";
        vm.HostedGameVersionSource = Loc.T("hosted.versionSourceFile");
        Assert.Equal("1.22.7", vm.GameVersionText);
        Assert.Equal(0, changes); // найденная версия — не правка профиля

        vm.ManualGameVersion = " 1.21.5 ";
        Assert.Equal("1.21.5", vm.Model.Hosted.GameVersion);
        Assert.Equal("1.21.5", vm.GameVersionText); // указанная вручную главнее найденной
        vm.ManualGameVersion = "";
        Assert.Null(vm.Model.Hosted.GameVersion);
        Assert.Equal(2, changes);

        Assert.True(vm.RemindStopServer); // по умолчанию напоминаем
        vm.RemindStopServer = false;
        Assert.True(vm.Model.Hosted.SkipStopReminder);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void EditConnection_ReplacesTheConnection_OrKeepsItOnCancel()
    {
        var changes = 0;
        var vm = new ProfileViewModel(Hosted(), _ => changes++);
        var asked = new List<object?[]>();
        HostedConnection? answer = null;
        AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of(("AskHostedConnection", args => { asked.Add(args); return answer; }));

        vm.EditConnectionCommand.Execute(null);
        Assert.Equal("198.51.100.7", vm.Model.Hosted!.Host); // закрыли — без изменений
        Assert.Equal(0, changes);
        Assert.Same(vm.Model.Hosted, Assert.Single(asked)[0]);
        Assert.Equal(Loc.T("settings.hostedEditOk"), asked[0][1]);

        answer = new HostedConnection { Host = "203.0.113.9", Port = 21, User = "new" };
        vm.EditConnectionCommand.Execute(null);
        Assert.Same(answer, vm.Model.Hosted);
        Assert.Equal(GameProfile.HostedMarker, vm.Model.RemoteCode);
        Assert.StartsWith("ftp://new@203.0.113.9:21", vm.HostedTarget);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ExistingProfiles_KeepTheirFlags()
    {
        var local = new ProfileViewModel(new GameProfile { Name = "Клиент", Kind = ProfileKind.Client }, _ => { });
        Assert.True(local.IsLocal);
        Assert.False(local.IsHosted);
        Assert.Equal("", local.ManualGameVersion);
        Assert.True(local.RemindStopServer);
        local.ManualGameVersion = "1.0.0"; // у не-хостового профиля — ничего не делает
        Assert.Null(local.Model.Hosted);

        var agent = new ProfileViewModel(new GameProfile { Name = "Дальний", Kind = ProfileKind.Server, RemoteCode = "не-код" }, _ => { });
        Assert.True(agent.IsRemote);
        Assert.False(agent.IsLocal);
        Assert.False(agent.IsHosted);
        Assert.Equal(Loc.T("profile.kindRemote"), agent.KindText);
    }
}

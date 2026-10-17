using System.ComponentModel;
using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>Подсказка под полем «Версия игры сервера» у профиля у хостера: собирается из найденной версии и её источника.</summary>
public sealed class HostedGameVersionHintTests
{
    private static ProfileViewModel Hosted() => new(new GameProfile
    {
        Name = "G-Portal", Kind = ProfileKind.Server, RemoteCode = GameProfile.HostedMarker,
        Hosted = new HostedConnection { Host = "198.51.100.7", Port = 58021, User = "gp_user" },
    }, _ => { });

    [Fact]
    public void BeforeReadingTheMods_SaysItWillFindOut()
    {
        Assert.Equal(Loc.T("settings.hostedGameVersionHint", Loc.T("profile.hostedVersion")), Hosted().HostedGameVersionHint);
    }

    [Fact]
    public void FoundVersion_IsShownWithItsSource_AndTheHintIsAnnounced()
    {
        var vm = Hosted();
        var announced = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ProfileViewModel.HostedGameVersionHint)) announced++; };

        vm.HostedGameVersion = "1.22.7";
        Assert.Equal(Loc.T("settings.hostedGameVersionHint", "1.22.7"), vm.HostedGameVersionHint);

        vm.HostedGameVersionSource = Loc.T("hosted.versionSourceFile");
        Assert.Equal(Loc.T("settings.hostedGameVersionHint", "1.22.7 (" + Loc.T("hosted.versionSourceFile") + ")"), vm.HostedGameVersionHint);
        Assert.Equal(2, announced);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EverythingChanged_AlsoAnnouncesTheHint(string? name)
    {
        var vm = new Probe(Hosted());
        vm.Raise(name);
        Assert.Contains(nameof(ProfileViewModel.HostedGameVersionHint), vm.Seen);
    }

    /// <summary>Поднять у модели «поменялось всё» (null или пустое имя) — так делает разметка при смене профиля целиком.</summary>
    private sealed class Probe
    {
        private readonly ProfileViewModel _vm;
        public List<string?> Seen { get; } = [];

        public Probe(ProfileViewModel vm)
        {
            _vm = vm;
            vm.PropertyChanged += (_, e) => Seen.Add(e.PropertyName);
        }

        public void Raise(string? name) =>
            typeof(ProfileViewModel).GetMethod("OnPropertyChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                [typeof(PropertyChangedEventArgs)])!.Invoke(_vm, [new PropertyChangedEventArgs(name)]);
    }
}

using eViSTool.Core.Localization;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.App.Tests;

/// <summary>Окно выбора версии мода: пустой список, «Установить» без выбора, ответ окну.</summary>
public sealed class RollbackViewModelTests
{
    private static VersionOption Option(string version, string source = "ModDB") =>
        new(version, source, "10.10.2026", "", null, null);

    [Fact]
    public void NoVersions_SaysSo_AndInstallStaysSilent()
    {
        var vm = new RollbackViewModel("Мод — сейчас 1.0.0", []);
        var accepted = new List<VersionOption>();
        vm.Accepted += accepted.Add;

        Assert.Empty(vm.Options);
        Assert.Equal(Loc.T("rollback.none"), vm.Status);

        vm.InstallCommand.Execute(null);

        Assert.Empty(accepted);
        Assert.Equal(Loc.T("rollback.pick"), vm.Status);
    }

    [Fact]
    public void Versions_NoStatus_UntilInstallWithoutSelection()
    {
        var vm = new RollbackViewModel("Мод — сейчас 1.0.0", [Option("0.9.0"), Option("0.8.0")]);
        var accepted = new List<VersionOption>();
        vm.Accepted += accepted.Add;

        Assert.Equal("Мод — сейчас 1.0.0", vm.Heading);
        Assert.Equal(["0.9.0", "0.8.0"], vm.Options.Select(o => o.Version));
        Assert.Equal("", vm.Status);
        Assert.Null(vm.Selected);

        vm.InstallCommand.Execute(null);

        // ничего не выбрано — окно не закрывается, строка у кнопок просит выбрать
        Assert.Empty(accepted);
        Assert.Equal(Loc.T("rollback.pick"), vm.Status);
    }

    [Fact]
    public void Install_GivesTheSelectedVersionToTheWindow()
    {
        var first = Option("0.9.0", Loc.T("rollback.savedCopy"));
        var second = Option("0.8.0");
        var vm = new RollbackViewModel("Мод — сейчас 1.0.0", [first, second]);
        var accepted = new List<VersionOption>();
        vm.Accepted += accepted.Add;

        vm.Selected = second;
        vm.InstallCommand.Execute(null);

        Assert.Same(second, Assert.Single(accepted));
    }

    [Fact]
    public void Options_AreCopied_NotTiedToTheSource()
    {
        var source = new List<VersionOption> { Option("0.9.0") };
        var vm = new RollbackViewModel("Мод — сейчас 1.0.0", source);

        source.Add(Option("0.8.0"));

        Assert.Single(vm.Options);
    }
}

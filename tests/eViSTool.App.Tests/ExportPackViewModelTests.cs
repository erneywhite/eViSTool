using eViSTool.Core.Localization;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.App.Tests;

/// <summary>Окно «Создать модпак»: параметры собираются из полей, пустое название не пускает, настройки модов — только если они есть.</summary>
public sealed class ExportPackViewModelTests
{
    private static ExportPackViewModel Make(bool hasModConfig = true) => new("Выживание", 24, 3, hasModConfig);

    [Fact]
    public void Defaults_MatchTheOldWindow()
    {
        var vm = Make();

        Assert.Equal("Выживание", vm.Name);
        Assert.Equal("", vm.Description);
        Assert.True(vm.BundleFiles);          // файлы внутрь — включено с самого начала
        Assert.False(vm.IncludeModConfig);
        Assert.False(vm.IncludeDisabled);
        Assert.True(vm.HasName);
        Assert.Equal(Loc.T("packx.summary", 24, 3), vm.Summary);
    }

    [Fact]
    public void Build_WithDefaults_ReturnsNameAndTheBundleFlag()
    {
        var options = Make().Build();

        Assert.NotNull(options);
        Assert.Equal("Выживание", options.Name);
        Assert.Null(options.Description);
        Assert.True(options.BundleFiles);
        Assert.False(options.IncludeModConfig);
        Assert.False(options.IncludeDisabled);
    }

    [Fact]
    public void Build_TrimsNameAndDescription_AndTakesAllFlags()
    {
        var vm = Make();
        vm.Name = "  Сборка друзей \t";
        vm.Description = "\n Для вечерних посиделок  ";
        vm.BundleFiles = false;
        vm.IncludeModConfig = true;
        vm.IncludeDisabled = true;

        var options = vm.Build();

        Assert.NotNull(options);
        Assert.Equal("Сборка друзей", options.Name);
        Assert.Equal("Для вечерних посиделок", options.Description);
        Assert.False(options.BundleFiles);
        Assert.True(options.IncludeModConfig);
        Assert.True(options.IncludeDisabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \t ")]
    [InlineData(null)]
    public void Build_WithoutAName_ReturnsNull(string? name)
    {
        var vm = Make();
        vm.Name = name!;

        Assert.False(vm.HasName);
        Assert.Null(vm.Build());
    }

    [Fact]
    public void HasName_FollowsTheName()
    {
        var vm = Make();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Name = "";
        Assert.False(vm.HasName);
        vm.Name = "Пак";
        Assert.True(vm.HasName);

        Assert.Contains(nameof(ExportPackViewModel.HasName), changed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n ")]
    public void Build_BlankDescription_BecomesNull(string description)
    {
        var vm = Make();
        vm.Description = description;

        Assert.Null(vm.Build()!.Description);
    }

    // Галочка «Настройки модов» без папки ModConfig выключена и поставить её нельзя: что бы ни лежало в свойстве, в пак она не идёт
    [Fact]
    public void ModConfig_WithoutTheFolder_IsNeverIncluded()
    {
        var vm = Make(hasModConfig: false);
        vm.IncludeModConfig = true;

        Assert.False(vm.HasModConfig);
        Assert.False(vm.Build()!.IncludeModConfig);
    }

    [Fact]
    public void ModConfig_WithTheFolder_FollowsTheCheckbox()
    {
        var vm = Make(hasModConfig: true);

        Assert.True(vm.HasModConfig);
        Assert.False(vm.Build()!.IncludeModConfig);
        vm.IncludeModConfig = true;
        Assert.True(vm.Build()!.IncludeModConfig);
    }

    [Fact]
    public void Summary_UsesTheCounts()
    {
        Assert.Equal(Loc.T("packx.summary", 0, 7), new ExportPackViewModel("x", 0, 7, false).Summary);
        Assert.Equal(Loc.T("packx.summary", 5, 0), new ExportPackViewModel("x", 5, 0, false).Summary);
    }
}

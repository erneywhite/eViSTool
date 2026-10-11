using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Localization;
using eViSTool.Core.Server.Config;
using eViSTool.Desktop.Controls;
using eViSTool.Desktop.Converters;
using eViSTool.Desktop.Views;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>Вкладка «Роли»: сетка привилегий (ColumnsPanel), образец цвета и сама вкладка с лесной темой.</summary>
public sealed class RolesViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evistool-tests", "roles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    // ---- ColumnsPanel

    private static ColumnsPanel Panel(int count, Func<int, double> height)
    {
        var panel = new ColumnsPanel { MinColumnWidth = 250, MaxColumns = 3 };
        // MinHeight, а не Height: ячейка растягивается на всю строку, и Bounds — это слот ячейки
        for (var i = 0; i < count; i++) panel.Children.Add(new Border { MinHeight = height(i) });
        return panel;
    }

    [AvaloniaTheory]
    [InlineData(900, 3)]
    [InlineData(749, 2)]
    [InlineData(500, 2)]
    [InlineData(260, 1)]
    [InlineData(100, 1)] // уже одной колонки — всё равно одна
    public void Columns_fit_width_up_to_max(double width, int columns)
    {
        var panel = Panel(5, _ => 20);
        panel.Measure(new Size(width, double.PositiveInfinity));
        Assert.Equal(columns, panel.Columns);
        Assert.Equal(width, panel.DesiredSize.Width);
    }

    [AvaloniaFact]
    public void Row_is_as_tall_as_its_tallest_cell()
    {
        // 3 колонки: строки (10, 40, 20) и (30, 5) → 40 + 30
        double[] heights = [10, 40, 20, 30, 5];
        var panel = Panel(heights.Length, i => heights[i]);
        panel.Measure(new Size(900, double.PositiveInfinity));
        Assert.Equal(70, panel.DesiredSize.Height);

        panel.Arrange(new Rect(0, 0, 900, 70));
        var cells = panel.Children.Select(c => c.Bounds).ToList();
        Assert.Equal(new Rect(0, 0, 300, 40), cells[0]);
        Assert.Equal(new Rect(600, 0, 300, 40), cells[2]);
        Assert.Equal(new Rect(0, 40, 300, 30), cells[3]);
        Assert.Equal(new Rect(300, 40, 300, 30), cells[4]);
    }

    [AvaloniaFact]
    public void Unbounded_width_takes_max_columns_of_min_width()
    {
        var panel = Panel(4, _ => 10);
        panel.Measure(Size.Infinity);
        Assert.Equal(3, panel.Columns);
        Assert.Equal(new Size(750, 20), panel.DesiredSize);
    }

    [AvaloniaFact]
    public void Changing_max_columns_remeasures()
    {
        var panel = Panel(4, _ => 10);
        panel.VerticalAlignment = VerticalAlignment.Top;
        var window = new Window { Width = 1000, Height = 300, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, panel.Columns);

        panel.MaxColumns = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, panel.Columns);
        Assert.Equal(20, panel.Bounds.Height);
        window.Close();
    }

    // ---- RoleColorBrushConverter

    [Fact]
    public void Color_becomes_brush_and_unknown_stays_transparent()
    {
        var brush = RoleColorBrushConverter.Instance.Convert(new RoleRgb(0x7F, 0xB0, 0x69), typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(Color.FromRgb(0x7F, 0xB0, 0x69), Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);

        RoleRgb? none = null;
        Assert.Same(Brushes.Transparent, RoleColorBrushConverter.Instance.Convert(none, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    // ---- сама вкладка

    private const string Config = """
        {
          "ServerName": "Стенд",
          "DefaultRoleCode": "suplayer",
          "Roles": [
            { "Code": "suvisitor", "Name": "Survival Visitor", "PrivilegeLevel": -1, "Color": "Green", "DefaultGameMode": 7, "Privileges": [ "chat" ] },
            { "Code": "suplayer", "Name": "Survival Player", "PrivilegeLevel": 0, "Color": "White",
              "LandClaimAllowance": 262144, "LandClaimMaxAreas": 3, "LandClaimMinSize": { "X": 5, "Y": 5, "Z": 5 },
              "Privileges": [ "build", "useblock", "chat", "carryon:carry" ], "RuntimePrivileges": [ "mymod.fly" ] },
            { "Code": "vip", "Name": "Меценат", "PrivilegeLevel": 50, "Color": "#7FB069", "DefaultGameMode": 2, "AutoGrant": true, "Privileges": [ "build" ] }
          ]
        }
        """;

    private (Window Window, RolesView View, RolesEditorViewModel Model, Action Restore) Show()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "serverconfig.json");
        File.WriteAllText(path, Config);
        var model = new RolesEditorViewModel(() => { }) { IsReadOnly = false };
        model.Attach(ServerConfigDocument.Load(path));

        // лесная тема, как у окна: ресурсы (кисти, иконки, шаблоны) и классы. Ресурсы — в приложение: StaticResource
        // в разметке вкладки ищет их уже при загрузке, до того как вкладка окажется в окне
        var app = Application.Current!;
        var baseUri = new Uri("avares://eViSTool.Desktop.Tests");
        var resources = new ResourceInclude(baseUri) { Source = new Uri("avares://eViSTool.Desktop/Themes/ForestResources.axaml") };
        var controls = new ResourceInclude(baseUri) { Source = new Uri("avares://eViSTool.Desktop/Themes/ForestControls.axaml") };
        var styles = new StyleInclude(baseUri) { Source = new Uri("avares://eViSTool.Desktop/Themes/Forest.axaml") };
        app.Resources.MergedDictionaries.Add(resources);
        app.Resources.MergedDictionaries.Add(controls);
        app.Styles.Add(styles);
        void Restore()
        {
            app.Styles.Remove(styles);
            app.Resources.MergedDictionaries.Remove(controls);
            app.Resources.MergedDictionaries.Remove(resources);
        }

        try
        {
            var view = new RolesView { DataContext = model };
            var window = new Window { Width = 1200, Height = 800, Content = view, RequestedThemeVariant = ThemeVariant.Dark };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return (window, view, model, Restore);
        }
        catch
        {
            Restore();
            throw;
        }
    }

    private static T ById<T>(Visual root, string id) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);

    [AvaloniaFact]
    public void Shows_selected_role_and_list()
    {
        var (window, view, model, restore) = Show();
        try
        {
            Assert.Equal("suplayer", model.SelectedRole?.Code);
            Assert.Equal("suplayer", ById<TextBox>(view, "RoleCode").Text);
            Assert.True(ById<TextBox>(view, "RoleCode").IsReadOnly); // стандартная роль: код не правится

            // строки списка: имя элемента для автоматизации — код роли
            var list = ById<ListBox>(view, "RolesList");
            var names = Enumerable.Range(0, model.Roles.Count).Select(i => AutomationProperties.GetName(list.ContainerFromIndex(i)!)).ToList();
            Assert.Equal(["suvisitor", "suplayer", "vip"], names);

            // роль для новых игроков
            Assert.Same(model.Roles[1], ById<ComboBox>(view, "DefaultRole").SelectedItem);

            // сетка привилегий — ColumnsPanel, переключатель у каждой привилегии; привилегия мода тоже видна
            var grid = view.GetVisualDescendants().OfType<ColumnsPanel>().Single();
            Assert.Equal(model.SelectedRole!.Privileges.Count, grid.Children.Count);
            Assert.True(grid.Columns >= 2);
            Assert.True(ById<CheckBox>(view, "Priv_build").IsChecked);
            Assert.True(ById<CheckBox>(view, "Priv_carryon:carry").IsChecked);
            Assert.False(ById<CheckBox>(view, "Priv_root").IsChecked);
        }
        finally
        {
            window.Close();
            restore();
        }
    }

    [AvaloniaFact]
    public void Note_keeps_its_own_color_over_hint_class()
    {
        var (window, view, _, restore) = Show();
        try
        {
            var note = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == Loc.T("roles.unusedNote"));
            var hint = view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == Loc.T("roles.colorHint"));
            var autoHint = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == Loc.T("roles.autoGrantHint"));
            var warn = (ISolidColorBrush)Application.Current!.FindResource(ThemeVariant.Dark, "Forest.Warn")!;
            var muted = (ISolidColorBrush)Application.Current!.FindResource(ThemeVariant.Dark, "Forest.Muted")!;

            // предупреждение — цветом Warn (в шаблоне атрибут Foreground с DynamicResource проиграл бы классу hint)
            Assert.Equal(warn.Color, ((ISolidColorBrush)note.Foreground!).Color);
            Assert.Equal(muted.Color, ((ISolidColorBrush)hint.Foreground!).Color);
            // пояснение под подписью — с отступом снизу, а где отступ задан на месте, остаётся он
            Assert.Equal(new Thickness(0, 2, 0, 6), hint.Margin);
            Assert.Equal(new Thickness(0, 2, 0, 0), autoHint.Margin);
            Assert.Equal(new Thickness(0, 2, 0, 0), note.Margin);
        }
        finally
        {
            window.Close();
            restore();
        }
    }

    /// <summary>
    /// Цвет — поле со списком и свободным вводом. Ввод доходит до модели и образца; смена роли меняет текст поля,
    /// но в соседнюю роль ничего не пишет (шаблон правой части один на все роли и только меняет DataContext).
    /// </summary>
    [AvaloniaFact]
    public void Color_box_edits_selected_role_only()
    {
        var (window, view, model, restore) = Show();
        try
        {
            var player = model.Roles.Single(r => r.Code == "suplayer");
            var vip = model.Roles.Single(r => r.Code == "vip");
            var box = ById<ComboBox>(view, "RoleColor");
            Assert.Equal("White", box.Text);
            Assert.Equal("White", ((RoleColorName)box.SelectedItem!).Name);

            // с цвета из списка на свой: выбор элемента сбрасывается, а текст остаётся цветом новой роли
            model.SelectedRole = vip;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(box, ById<ComboBox>(view, "RoleColor")); // то же поле, сменилась только роль
            Assert.Equal("#7FB069", box.Text);
            Assert.Equal("#7FB069", vip.ColorText);
            Assert.Null(vip.ColorError);
            Assert.Null(box.SelectedItem);
            Assert.Equal("White", player.ColorText);
            Assert.All(model.Roles, r => Assert.False(r.IsChanged));

            // выбор из списка пишет название; свой ввод — как есть, ошибка — под полем
            box.SelectedItem = RoleCatalog.FindColor("Gold");
            Assert.Equal("Gold", vip.ColorText);
            Assert.Equal("Gold", vip.Entry.Color);
            box.SetCurrentValue(ComboBox.TextProperty, "#12345"); // так текст меняет само поле при вводе
            Assert.Equal("#12345", vip.ColorText);
            Assert.NotNull(vip.ColorError);
            Dispatcher.UIThread.RunJobs();
            var error = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == vip.ColorError);
            Assert.True(error.IsEffectivelyVisible);
            box.SetCurrentValue(ComboBox.TextProperty, "#123456"); // так текст меняет само поле при вводе
            Assert.Null(vip.ColorError);
            Assert.Equal("#123456", vip.Entry.Color);

            // образец у поля и метка в списке — новым цветом
            Dispatcher.UIThread.RunJobs();
            var swatches = view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("swatch") && b.Background is ISolidColorBrush { Color: var c } && c == Color.FromRgb(0x12, 0x34, 0x56));
            Assert.Equal(2, swatches.Count());

            model.SelectedRole = player;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("White", ById<ComboBox>(view, "RoleColor").Text);
            Assert.Equal("#123456", vip.ColorText);
            Assert.False(player.IsChanged);
        }
        finally
        {
            window.Close();
            restore();
        }
    }

    /// <summary>
    /// Перебор ролей ничего не правит: поля правой части при смене роли получают её значения и обратно в модель пишут
    /// только их же (в том числе режим игры не из списка и цвет не из списка).
    /// </summary>
    [AvaloniaFact]
    public void Switching_roles_changes_nothing()
    {
        var (window, view, model, restore) = Show();
        try
        {
            for (var round = 0; round < 2; round++)
                foreach (var role in model.Roles.Reverse().ToList())
                {
                    model.SelectedRole = role;
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(role.ColorText, ById<ComboBox>(view, "RoleColor").Text);
                    Assert.Same(role.SelectedGameMode, ById<ComboBox>(view, "RoleGameMode").SelectedItem);
                    Assert.Equal(role.NameText, ById<TextBox>(view, "RoleName").Text);
                }

            Assert.All(model.Roles, r => Assert.False(r.IsChanged));
            Assert.Equal(7, model.Roles[0].Entry.DefaultGameMode);
            Assert.Equal(2, model.Roles[2].Entry.DefaultGameMode);
            Assert.Equal(["Green", "White", "#7FB069"], model.Roles.Select(r => r.Entry.Color));
        }
        finally
        {
            window.Close();
            restore();
        }
    }

    [AvaloniaFact]
    public void Read_only_locks_fields_and_switches()
    {
        var (window, view, model, restore) = Show();
        try
        {
            Assert.True(ById<CheckBox>(view, "Priv_build").IsEnabled);
            Assert.False(ById<TextBox>(view, "RoleName").IsReadOnly);

            model.IsReadOnly = true; // сервер запустили
            Dispatcher.UIThread.RunJobs();
            Assert.False(ById<CheckBox>(view, "Priv_build").IsEnabled);
            Assert.False(ById<CheckBox>(view, "RoleAutoGrant").IsEnabled);
            Assert.False(ById<ComboBox>(view, "RoleColor").IsEnabled);
            Assert.False(ById<ComboBox>(view, "DefaultRole").IsEnabled);
            Assert.True(ById<TextBox>(view, "RoleName").IsReadOnly);
            Assert.False(ById<Button>(view, "RoleDuplicate").IsEffectivelyEnabled);
            Assert.False(ById<Button>(view, "PrivGrantAll").IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
            restore();
        }
    }
}

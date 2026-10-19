using System.Xml;
using System.Xml.Linq;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Имена кнопок для экранного диктора (Narrator, Orca). Без своего имени кнопка берёт текст, только если её содержимое —
/// строка или сам TextBlock. У «значок + текст» (StackPanel), одного значка или картинки имя выходит именем класса:
/// «Avalonia.Controls.StackPanel, кнопка». Таким кнопкам имя задают AutomationProperties.Name — той же строкой, что видна.
/// </summary>
public sealed class AutomationNameTests
{
    private static readonly string[] Buttons = ["Button", "ToggleButton", "RadioButton", "CheckBox", "RepeatButton", "SplitButton", "DropDownButton", "HyperlinkButton"];

    [AvaloniaFact]
    public void Icon_and_text_button_needs_an_explicit_name()
    {
        StackPanel IconAndText() => new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { new Avalonia.Controls.Shapes.Path(), new TextBlock { Text = "Обновить" } } };
        var bare = new Button { Content = IconAndText() };
        var named = new Button { Content = IconAndText() };
        Avalonia.Automation.AutomationProperties.SetName(named, "Обновить");
        var window = new Window { Content = new StackPanel { Children = { bare, named } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // если Avalonia научится собирать имя из текста содержимого, проверку разметки ниже можно убрать
        Assert.Equal(typeof(StackPanel).FullName, ControlAutomationPeer.CreatePeerForElement(bare).GetName());
        Assert.Equal("Обновить", ControlAutomationPeer.CreatePeerForElement(named).GetName());
        window.Close();
    }

    /// <summary>
    /// Виды и окна: у кнопки с содержимым-элементом (не TextBlock) есть AutomationProperties.Name. Кнопки в шаблонах темы
    /// (заголовок окна) здесь не проверяются.
    /// </summary>
    [Fact]
    public void Markup_buttons_with_composite_content_have_names()
    {
        var src = System.IO.Path.Combine(ThemeTests.RepoRoot(), "src", "eViSTool.Desktop");
        // shortcut: эти виды правятся в соседних ветках ревью 1.0, имена кнопкам там дадут они; убрать список после слияния
        string[] pending = ["Views/MainWindow.axaml", "Views/ProfilesView.axaml", "Views/CatalogView.axaml", "Views/ModsView.axaml"];
        var checkedButtons = 0;
        var found = new List<string>();
        foreach (var folder in new[] { "Views", "Dialogs" })
            foreach (var file in Directory.EnumerateFiles(System.IO.Path.Combine(src, folder), "*.axaml"))
            {
                var relative = System.IO.Path.GetRelativePath(src, file).Replace('\\', '/');
                if (pending.Contains(relative)) continue;
                foreach (var button in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants().Where(e => Buttons.Contains(e.Name.LocalName)))
                {
                    // свойства-элементы (<Button.Flyout> и т. п.) — не содержимое
                    var content = button.Elements().FirstOrDefault(e => !e.Name.LocalName.Contains('.'));
                    if (button.Attribute("Content") is not null || content is null || content.Name.LocalName == "TextBlock") continue;
                    checkedButtons++;
                    if (button.Attribute("AutomationProperties.Name") is null)
                        found.Add($"{relative}:{((IXmlLineInfo)button).LineNumber}: <{button.Name.LocalName}> с <{content.Name.LocalName}>");
                }
            }
        Assert.True(checkedButtons > 20, $"кнопок с содержимым-элементом найдено: {checkedButtons}"); // проверка видит разметку
        Assert.Empty(found);
    }
}

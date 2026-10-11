using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using eViSTool.Desktop.Controls;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

public class EnumClassTests
{
    private enum State { Idle, UpdateAvailable }

    [AvaloniaFact]
    public void Value_sets_class_and_removes_previous()
    {
        var dot = new Ellipse();
        dot.Classes.Add("tone");

        EnumClass.SetValue(dot, RowTone.Good);
        Assert.Equal(["tone", "good"], dot.Classes);

        EnumClass.SetValue(dot, RowTone.Warn);
        Assert.Equal(["tone", "warn"], dot.Classes);

        EnumClass.SetValue(dot, null);
        Assert.Equal(["tone"], dot.Classes);
    }

    [AvaloniaFact]
    public void Class_name_is_lower_camel_case()
    {
        Assert.Equal("updateAvailable", EnumClass.ClassName(State.UpdateAvailable));
        Assert.Equal("idle", EnumClass.ClassName(State.Idle));
        Assert.Null(EnumClass.ClassName("Good")); // не перечисление
        Assert.Null(EnumClass.ClassName((State)42)); // значение без имени
    }
}

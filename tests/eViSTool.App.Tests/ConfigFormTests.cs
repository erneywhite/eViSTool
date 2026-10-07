using eViSTool.App.ViewModels;
using eViSTool.Core.Profiles;

namespace eViSTool.App.Tests;

/// <summary>Форма конфига: поля по типам, правка поля меняет дерево JSON, ошибка — не меняет.</summary>
public sealed class ConfigFormTests
{
    private const string Json = """
        {
          "Sprint": false,
          "Speed": 1.0,
          "Stack": 4,
          "Mode": "normal",
          "Owner": null,
          "List": ["a", "b"],
          "Drop": { "On": true, "Limit": 2.5 }
        }
        """;

    private static (Newtonsoft.Json.Linq.JToken Root, List<ConfigField> Fields) Form()
    {
        var root = ModConfigs.ParseJson(Json);
        return (root, ConfigForm.Build(root, () => { }));
    }

    [Fact]
    public void FieldsMatchTheValueTypes()
    {
        var (_, fields) = Form();
        Assert.Equal(["Sprint", "Speed", "Stack", "Mode", "Owner", "List", "Drop", "On", "Limit"], fields.Select(f => f.Key));
        Assert.IsType<BoolField>(fields[0]);
        Assert.IsType<NumberField>(fields[1]);
        Assert.IsType<TextField>(fields[4]); // null — текстовое поле
        Assert.IsType<JsonField>(fields[5]);
        Assert.IsType<GroupField>(fields[6]);
        Assert.Equal(1, fields[7].Depth);
    }

    [Fact]
    public void Edits_GoIntoTheJson()
    {
        var (root, fields) = Form();
        ((BoolField)fields[0]).Value = true;
        ((NumberField)fields[1]).Value = "1,5";          // запятая — как точка
        ((NumberField)fields[8]).Value = "3";            // дробное остаётся дробным
        ((JsonField)fields[5]).Value = """["a", "b", "c"]""";

        Assert.True((bool)root["Sprint"]!);
        Assert.Equal(1.5, (double)root["Speed"]!);
        Assert.Contains("\"Limit\": 3.0", ModConfigs.Format(root, Json));
        Assert.Equal(3, root["List"]!.Count());
        Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Null, root["Owner"]!.Type); // не трогали — так и null
    }

    [Fact]
    public void BadInput_ShowsAnError_AndLeavesTheJsonAlone()
    {
        var (root, fields) = Form();
        var stack = (NumberField)fields[2];
        stack.Value = "4.5"; // целое поле
        Assert.NotEmpty(stack.Error);
        Assert.Equal(4, (int)root["Stack"]!);

        var list = (JsonField)fields[5];
        list.Value = "[\"a\", ";
        Assert.NotEmpty(list.Error);
        Assert.Equal(2, root["List"]!.Count());
    }
}

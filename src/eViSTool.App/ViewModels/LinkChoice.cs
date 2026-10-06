using CommunityToolkit.Mvvm.ComponentModel;

namespace eViSTool.App.ViewModels;

/// <summary>Строка «связанные профили» в настройках: галочка сразу сохраняет связь.</summary>
public sealed partial class LinkChoice(string name, string kindText, bool isLinked, Action<bool> changed) : ObservableObject
{
    public string Name { get; } = name;
    public string KindText { get; } = kindText;

    [ObservableProperty] private bool _isLinked = isLinked;

    partial void OnIsLinkedChanged(bool value) => changed(value);
}

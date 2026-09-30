using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Core.Server.Config;

namespace eViSTool.App.ViewModels;

/// <summary>Вкладка «Роли» редактора serverconfig: список ролей и редактор выбранной. ЗАГЛУШКА — шов с редактором конфига.</summary>
public sealed partial class RolesEditorViewModel : ObservableObject
{
    private readonly Action _changed;

    /// <param name="changed">Вызывать после каждой правки, записанной в документ (родитель пересчитает «несохранённость» и ошибки).</param>
    public RolesEditorViewModel(Action changed) => _changed = changed;

    /// <summary>Сервер работает — всё только для чтения.</summary>
    [ObservableProperty] private bool _isReadOnly;

    /// <summary>Новый документ (или null — файла нет): перестроить список ролей. Вызывается при загрузке, отмене правок и сохранении.</summary>
    public void Attach(ServerConfigDocument? document)
    {
    }

    /// <summary>Тексты ошибок (уже локализованные). Пусто — можно сохранять.</summary>
    public IReadOnlyList<string> Validate() => [];
}

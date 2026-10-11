using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Окно выбора версии мода: сохранённые копии и релизы модбазы для ветки игры. Строка не выбрана — «Установить» не
/// закрывает окно, а просит выбрать; выбрана — окно получает ответ через <see cref="Accepted"/>.
/// </summary>
public sealed partial class RollbackViewModel : ObservableObject
{
    public RollbackViewModel(string heading, IEnumerable<VersionOption> options)
    {
        Heading = heading;
        Options = [.. options];
        Status = Options.Count == 0 ? Loc.T("rollback.none") : "";
    }

    /// <summary>Заголовок: «Мод — сейчас 1.2.3».</summary>
    public string Heading { get; }

    public IReadOnlyList<VersionOption> Options { get; }

    /// <summary>Выбранная строка таблицы (null — ничего не выбрано).</summary>
    [ObservableProperty] private VersionOption? _selected;

    /// <summary>Строка у кнопок: «Версий нет» при пустом списке, «Выбери версию» — если нажали «Установить» без выбора.</summary>
    [ObservableProperty] private string _status;

    /// <summary>Выбранная версия принята (кнопка или двойной щелчок по строке): окно закрывается с этим ответом.</summary>
    public event Action<VersionOption>? Accepted;

    [RelayCommand]
    private void Install()
    {
        if (Selected is not { } option)
        {
            Status = Loc.T("rollback.pick");
            return;
        }
        Accepted?.Invoke(option);
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Строка окна «Установить также в»: профиль, что в него встанет (или почему нельзя), отмечен ли.</summary>
public sealed partial class AlsoInstallRowViewModel : ObservableObject
{
    public required string Name { get; init; }
    public required string KindText { get; init; }
    public required string Detail { get; init; }

    /// <summary>Можно ли менять галочку: текущий профиль отмечен всегда, недоступные (нет версии, нет связи) — без галочки.</summary>
    public bool IsEnabled { get; init; }

    /// <summary>Что ставить в этот профиль (<see cref="AlsoChoice.Tag"/>); у текущего профиля — null.</summary>
    public object? Tag { get; init; }

    [ObservableProperty] private bool _isChecked;
}

/// <summary>
/// Окно «Установить также в»: мод ставится в текущий профиль и, по галочкам, в другие (сервер ↔ клиент) —
/// каждому своя версия под его игру (см. <see cref="AlsoInstall"/>). Модель держит строки с галочками и «запомнить»;
/// «Установить» собирает из них ответ для <see cref="AlsoInstallPicker"/>.
/// </summary>
public sealed partial class AlsoInstallViewModel : ObservableObject
{
    /// <param name="others">Другие профили; Linked — связан с текущим (отмечен заранее, если туда можно поставить).</param>
    public AlsoInstallViewModel(string hint, string currentName, string currentKind, string currentDetail, IEnumerable<AlsoChoice> others)
    {
        Hint = hint;
        RememberText = Loc.T("also.remember", currentName);
        Rows =
        [
            new AlsoInstallRowViewModel { Name = currentName, KindText = currentKind, Detail = currentDetail, IsEnabled = false, IsChecked = true },
            .. others.Select(o => new AlsoInstallRowViewModel
            {
                Name = o.Profile.Name, KindText = o.Profile.KindText, Detail = o.Detail, Tag = o.Tag, IsEnabled = o.CanInstall,
                IsChecked = o.Linked && o.CanInstall,
            }),
        ];
    }

    /// <summary>Пояснение под заголовком: какой мод и зачем отмечать профили.</summary>
    public string Hint { get; }

    /// <summary>«Запомнить: при установке в «…» всегда отмечать эти профили».</summary>
    public string RememberText { get; }

    /// <summary>Первая строка — текущий профиль, дальше — остальные.</summary>
    public IReadOnlyList<AlsoInstallRowViewModel> Rows { get; }

    /// <summary>Запомнить отмеченное как связь текущего профиля (в следующий раз отмечено заранее).</summary>
    [ObservableProperty] private bool _remember;

    /// <summary>Отмеченные профили, кроме текущего, — их Tag. Недоступные сюда не попадают, даже если галочка стоит.</summary>
    public IReadOnlyList<object> Chosen =>
        [.. Rows.Where(r => r.IsChecked && r.IsEnabled && r.Tag is not null).Select(r => r.Tag!)];

    /// <summary>Ответ на «Установить».</summary>
    public AlsoInstallAnswer Confirm() => new(Chosen, Remember);
}

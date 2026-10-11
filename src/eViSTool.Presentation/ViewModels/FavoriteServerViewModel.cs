using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Окно «Сервер в избранном игры»: название, адрес (с портом или без) и пароль — как в меню самой игры. Новый сервер
/// или правка записи. «Сохранить» не гаснет: адрес проверяется по нажатию, и если он не годится, под полями появляется
/// причина (так же, как в окне на WPF). Название можно оставить пустым — им станет адрес, пустой пароль значит «без
/// пароля». Ответ готов — <see cref="Finished"/> с <see cref="FavoriteServerChoice"/>, окно закрывается с ним.
/// </summary>
public sealed partial class FavoriteServerViewModel : ObservableObject
{
    private string _errorText = "";

    /// <param name="existing">Правка записи избранного; null — новый сервер.</param>
    public FavoriteServerViewModel(PlayTarget? existing)
    {
        IsNew = existing is null;
        Heading = Loc.T(IsNew ? "browse.favAdd" : "browse.favEdit");
        _name = existing?.Name ?? "";
        _address = existing?.Address ?? "";
        // пароль из избранного игры — он там открытым текстом, в окне его прячут точками
        _password = existing?.Password ?? "";
    }

    // поле ввода может отдать null вместо пустой строки
    private static string Clean(string? text) => text?.Trim() ?? "";

    /// <summary>Новый сервер (поле названия в фокусе); false — правка записи (в фокусе адрес).</summary>
    public bool IsNew { get; }

    /// <summary>Крупная надпись в окне: «Добавить сервер» или «Изменить сервер».</summary>
    public string Heading { get; }

    /// <summary>Название сервера (как ввели; в ответ идёт без пробелов по краям, пустое — адрес).</summary>
    [ObservableProperty] private string _name;

    /// <summary>Адрес: IP или домен, по желанию с :портом (как ввели; в ответ идёт без пробелов по краям).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _address;

    /// <summary>Пароль сервера (секрет: в окне — поле с точками). Пустой — у сервера пароля нет.</summary>
    [ObservableProperty] private string _password;

    /// <summary>Адрес годится для записи (<see cref="GameFavorites.IsValidAddress"/>). Кнопка от этого не гаснет — см. <see cref="SaveCommand"/>.</summary>
    public bool CanSave => GameFavorites.IsValidAddress(Clean(Address));

    /// <summary>Почему не сохранилось; пусто, пока «Сохранить» не отказала. Исправленный адрес текст не убирает: он уходит со следующим нажатием.</summary>
    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => ErrorText.Length > 0;

    /// <summary>Ответ окна; null — ещё не сохраняли.</summary>
    public FavoriteServerChoice? Result { get; private set; }

    /// <summary>Сохранение удалось — окно закрывается с этим ответом.</summary>
    public event Action<FavoriteServerChoice>? Finished;

    /// <summary>
    /// «Сохранить»: адрес не годится — причина под полями, окно остаётся; иначе ответ окна. Команда доступна всегда:
    /// серая кнопка не объяснила бы, что не так с адресом.
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        var address = Clean(Address);
        if (!CanSave)
        {
            ErrorText = Loc.T("browse.favBadAddress");
            return;
        }
        ErrorText = "";
        var choice = new FavoriteServerChoice(
            Clean(Name) is { Length: > 0 } name ? name : address,
            address,
            Password is { Length: > 0 } password ? password : null);
        Result = choice;
        Finished?.Invoke(choice);
    }
}

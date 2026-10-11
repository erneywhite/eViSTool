using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Просмотр скриншотов мода: какой из адресов сейчас на экране, счётчик «2 / 5», листание по кругу и «Открыть в
/// браузере». Саму картинку по <see cref="CurrentUrl"/> грузит окно (оно знает, как; из модели видно только адрес).
/// Адреса приходят готовыми из карточки мода (<c>AppWindows.ShowScreenshots</c>).
/// </summary>
public sealed partial class ScreenshotViewerViewModel : ObservableObject
{
    private readonly IReadOnlyList<string> _urls;
    private int _index;

    /// <param name="urls">Адреса картинок по порядку.</param>
    /// <param name="index">С какой начать; вне списка — ближайшая крайняя.</param>
    /// <param name="title">Заголовок окна, обычно «Имя мода — скриншоты»; пустой — общее «Скриншоты».</param>
    public ScreenshotViewerViewModel(IReadOnlyList<string> urls, int index, string title)
    {
        _urls = urls;
        _index = urls.Count == 0 ? 0 : Math.Clamp(index, 0, urls.Count - 1);
        Title = string.IsNullOrWhiteSpace(title) ? Loc.T("shots.windowTitle") : title;
    }

    public string Title { get; }

    /// <summary>Сколько всего картинок.</summary>
    public int Count => _urls.Count;

    /// <summary>Номер показанной картинки, с нуля.</summary>
    public int Index => _index;

    /// <summary>Адрес показанной картинки; null — список пуст.</summary>
    public string? CurrentUrl => _urls.Count == 0 ? null : _urls[_index];

    /// <summary>«2 / 5»; для пустого списка — пустая строка.</summary>
    public string Counter => _urls.Count == 0 ? "" : $"{_index + 1} / {_urls.Count}";

    /// <summary>Картинок несколько: есть что листать (одну стрелки не нужны).</summary>
    public bool HasSeveral => _urls.Count > 1;

    /// <summary>Показать картинку по номеру; номер за краем списка заворачивается (−1 — последняя, за последней — первая).</summary>
    public void Show(int index)
    {
        if (_urls.Count == 0) return;
        var next = ((index % _urls.Count) + _urls.Count) % _urls.Count;
        if (next == _index) return;
        _index = next;
        OnPropertyChanged(nameof(Index));
        OnPropertyChanged(nameof(CurrentUrl));
        OnPropertyChanged(nameof(Counter));
    }

    [RelayCommand]
    private void Previous() => Show(_index - 1);

    [RelayCommand]
    private void Next() => Show(_index + 1);

    /// <summary>Открыть показанную картинку в браузере системы.</summary>
    [RelayCommand]
    private void OpenInBrowser()
    {
        if (CurrentUrl is { } url) Ui.OpenUrl(url);
    }
}

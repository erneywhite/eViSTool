using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Core.Localization;
using eViSTool.Core.Packs;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Окно «Вставить код набора»: поле для кода и строка под ним — сколько в нём модов или что с кодом не так.
/// «Поставить» доступно, только когда код разобран; ответ окна — список модов из кода.
/// </summary>
public sealed partial class ModCodeViewModel : ObservableObject
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private ModCodeSet? _set;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private RowTone _statusTone = RowTone.Muted;

    public bool CanAccept => Set is { Entries.Count: > 0 };

    partial void OnTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Set = null;
            (StatusText, StatusTone) = ("", RowTone.Muted);
        }
        else
        {
            try
            {
                Set = ModCode.Decode(value);
                var text = Loc.Plural("modcode.parsed", Set.Entries.Count);
                if (Set.GameVersion is { } game) text += " · " + Loc.T("modcode.parsedGame", game);
                (StatusText, StatusTone) = (text, RowTone.Good);
            }
            catch (FormatException ex)
            {
                Set = null;
                (StatusText, StatusTone) = (ex.Message, RowTone.Danger);
            }
        }
        OnPropertyChanged(nameof(CanAccept));
    }
}

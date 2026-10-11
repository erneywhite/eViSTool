using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Срок бана в окне: чипы «1 час», «1 день», «1 неделя», «30 дней», «Навсегда».</summary>
public enum BanTerm { Hour, Day, Week, Month, Forever }

/// <summary>
/// Окно бана игрока: срок (чипы) и причина. Срок уходит в команду /ban в её единицах (minute, hour, day, week, year):
/// «месяц» — 30 дней, «навсегда» — сто лет, так сервер и хранит бессрочный бан. Причина идёт без пробелов по краям.
/// «Забанить» — <see cref="Finished"/> с ответом (он же <see cref="Result"/>), окно закрывается с ним; закрыли без
/// неё — ответа нет.
/// </summary>
public sealed partial class BanViewModel : ObservableObject
{
    /// <param name="player">Ник игрока — в заголовке окна.</param>
    public BanViewModel(string player)
    {
        Heading = Loc.T("players.banHeading", player);
    }

    /// <summary>Крупная надпись в окне: «Забанить Вася».</summary>
    public string Heading { get; }

    /// <summary>Срок бана; по умолчанию — день.</summary>
    [ObservableProperty] private BanTerm _term = BanTerm.Day;

    /// <summary>Причина бана (игрок увидит её при входе); как ввели, с пробелами по краям — в ответ идёт без них.</summary>
    [ObservableProperty] private string _reason = "";

    /// <summary>Ответ окна после «Забанить»; пока не нажали — null.</summary>
    public BanChoice? Result { get; private set; }

    /// <summary>«Забанить» нажата — окно закрывается с этим ответом.</summary>
    public event Action<BanChoice>? Finished;

    /// <summary>Что получится при выбранном сроке и причине.</summary>
    public BanChoice Choice => ChoiceFor(Term, Reason);

    /// <summary>Срок в единицах команды /ban и причина без пробелов по краям (пустая причина — пустая строка).</summary>
    public static BanChoice ChoiceFor(BanTerm term, string? reason)
    {
        var (amount, unit) = term switch
        {
            BanTerm.Hour => (1, "hour"),
            BanTerm.Week => (1, "week"),
            BanTerm.Month => (30, "day"),
            BanTerm.Forever => (100, "year"),
            _ => (1, "day"),
        };
        return new BanChoice(amount, unit, reason?.Trim() ?? "");
    }

    [RelayCommand]
    private void Ok()
    {
        var choice = Choice;
        Result = choice;
        Finished?.Invoke(choice);
    }
}

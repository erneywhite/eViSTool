namespace eViSTool.Presentation.ViewModels;

/// <summary>Другой профиль в окне: что в него встанет (Detail), можно ли, связан ли с текущим; Tag — что ставить.</summary>
public sealed record AlsoChoice(ProfileViewModel Profile, object Tag, string Detail, bool CanInstall, bool AlreadyThere, bool Linked);

/// <summary>
/// «Установить также в»: мод ставится в текущий профиль и, по галочкам, в другие (сервер ↔ клиент) —
/// каждому своя версия под его игру (см. <see cref="AlsoInstall"/>).
/// </summary>
public static class AlsoInstallPicker
{
    /// <summary>
    /// Спросить, в какие ещё профили поставить. Пустой список — только сюда (некуда или не о чем), null — отмена.
    /// Связанные с текущим отмечены заранее; «без вопроса» в настройках — сразу в связанные, если во все можно
    /// (там, где уже стоит, пропуск не мешает). «Запомнить» сохраняет выбор как связь.
    /// </summary>
    public static IReadOnlyList<object>? Choose(MainViewModel main, ProfileViewModel active, string hint,
        string currentDetail, IReadOnlyList<AlsoChoice> others)
    {
        if (!others.Any(o => o.CanInstall)) return [];
        var linked = others.Where(o => o.Linked).ToList();
        if (main.AlsoInstallWithoutAsking && linked.Count > 0 && linked.All(o => o.CanInstall || o.AlreadyThere))
            return [.. linked.Where(o => o.CanInstall).Select(o => o.Tag)];

        var answer = AppWindows.Current.AskAlsoInstall(hint, active.Name, active.KindText, currentDetail, others);
        if (answer is null) return null;
        if (answer.Remember)
        {
            // связь — ровно с отмеченными; с остальными из окна — снять
            var chosen = answer.Chosen.ToHashSet();
            foreach (var o in others) active.Model.SetLinked(o.Profile.Model, chosen.Contains(o.Tag));
            main.SaveSettings();
            main.RefreshLinks();
        }
        return answer.Chosen;
    }
}

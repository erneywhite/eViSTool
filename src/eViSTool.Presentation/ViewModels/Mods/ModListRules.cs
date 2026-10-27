using eViSTool.Core.Mods;

namespace eViSTool.Presentation.ViewModels.Mods;

/// <summary>Правила таблицы «Моих модов»: какие строки видны при выбранном чипе и поиске и в каком они порядке.</summary>
internal static class ModListRules
{
    /// <summary>Строка проходит чип над таблицей и поиск (по названию и modid, без учёта регистра).</summary>
    public static bool Matches(ModRowViewModel r, ModFilter filter, string search) =>
        filter switch
        {
            ModFilter.Updates => r.Kind == ModStatus.UpdateAvailable,
            ModFilter.Problems => r.IsProblem,
            ModFilter.Pinned => r.IsPinned,
            ModFilter.Disabled => !r.IsEnabled,
            ModFilter.Unneeded => r.IsUnneeded,
            _ => true,
        }
        && (search.Length == 0
            || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || r.ModId.Contains(search, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Порядок строк: 0 — «Сначала важное» (проблемы, обновления…), внутри — по названию; иначе — по названию.
    /// Названия сравниваются с учётом языка системы. <paramref name="byCategory"/> — сперва по категории (по алфавиту, без
    /// категории — в конце): таблица собирает группы в порядке строк, так и группы встают по алфавиту.
    /// </summary>
    public static Comparison<ModRowViewModel> Order(int sortIndex, bool byCategory = false)
    {
        Comparison<ModRowViewModel> inside = sortIndex == 0
            ? (a, b) => a.Importance != b.Importance ? a.Importance.CompareTo(b.Importance) : string.Compare(a.Name, b.Name, StringComparison.CurrentCulture)
            : (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
        if (!byCategory) return inside;
        return (a, b) =>
        {
            if ((a.Category.Length == 0) != (b.Category.Length == 0)) return a.Category.Length == 0 ? 1 : -1;
            var c = string.Compare(a.Category, b.Category, StringComparison.CurrentCultureIgnoreCase);
            return c != 0 ? c : inside(a, b);
        };
    }
}

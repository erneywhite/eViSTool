namespace eViSTool.Core.ModDb;

/// <summary>Логотип мода с модбазы — адрес из её ответа (logofile мода, logo в каталоге).</summary>
public static class ModDbLogo
{
    /// <summary>
    /// Адрес, если у мода свой логотип. Пустой адрес и стандартная картинка модбазы (mod-default…png — её ставят модам
    /// без логотипа) — null: вместо безликой заглушки окно покажет иконку из файла мода или буквы цветом мода.
    /// </summary>
    public static string? Usable(string? url) =>
        string.IsNullOrWhiteSpace(url) || url.Contains("mod-default", StringComparison.OrdinalIgnoreCase) ? null : url.Trim();
}

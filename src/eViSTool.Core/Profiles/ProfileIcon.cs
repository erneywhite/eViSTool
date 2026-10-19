namespace eViSTool.Core.Profiles;

/// <summary>
/// Значок профиля: цвет плашки и знак на ней, по имени. Окно рисует их из темы (Forest.Badge.* и Badge.*); имена
/// не из списка (профиль из более новой версии окна) читаются как «не выбрано».
/// </summary>
public static class ProfileIcon
{
    public static readonly IReadOnlyList<string> Colors = ["moss", "pine", "teal", "sky", "plum", "rust", "amber", "stone"];

    /// <summary>Знаки; первые три — знаки видов профиля: клиентскому можно поставить знак сервера и наоборот
    /// (свой сервер, к которому клиент подключается по localhost).</summary>
    public static readonly IReadOnlyList<string> Glyphs =
        ["client", "server", "cloud", "sword", "pickaxe", "shield", "castle", "tree", "mountain", "flame", "house", "anchor", "skull", "gem", "flask"];

    /// <summary>Цвет из списка или null.</summary>
    public static string? Color(string? name) => name is not null && Colors.Contains(name) ? name : null;

    /// <summary>Знак из списка или null.</summary>
    public static string? Glyph(string? name) => name is not null && Glyphs.Contains(name) ? name : null;
}

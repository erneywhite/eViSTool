using System.Globalization;

namespace eViSTool.Core.Server.Config;

/// <summary>Цвет без прозрачности — чтобы нарисовать образец рядом с названием.</summary>
public readonly record struct RoleRgb(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>Название цвета, как его пишет сервер ("LightGreen"), и сам цвет.</summary>
public sealed record RoleColorName(string Name, RoleRgb Rgb)
{
    // в выпадающем списке с вводом текстом элемента считается его ToString
    public override string ToString() => Name;
}

/// <summary>Чем не годится код роли.</summary>
public enum RoleCodeProblem { None, Empty, Whitespace, Taken }

/// <summary>
/// Справочник для вкладки «Роли»: цвета, которые понимает сервер, режимы игры, привилегии сверх основного списка.
/// Только факты об игре — подписи лежат в словарях (priv.*, gamemode.*).
/// </summary>
public static class RoleCatalog
{
    /// <summary>
    /// Названия цветов .NET (без системных и Transparent) в том порядке, в каком их перечисляет сама платформа.
    /// Значения вшиты, чтобы не тащить System.Drawing ради образца цвета.
    /// </summary>
    public static IReadOnlyList<RoleColorName> Colors { get; } =
    [
        C("AliceBlue", 0xF0F8FF), C("AntiqueWhite", 0xFAEBD7), C("Aqua", 0x00FFFF), C("Aquamarine", 0x7FFFD4), C("Azure", 0xF0FFFF),
        C("Beige", 0xF5F5DC), C("Bisque", 0xFFE4C4), C("Black", 0x000000), C("BlanchedAlmond", 0xFFEBCD), C("Blue", 0x0000FF),
        C("BlueViolet", 0x8A2BE2), C("Brown", 0xA52A2A), C("BurlyWood", 0xDEB887), C("CadetBlue", 0x5F9EA0), C("Chartreuse", 0x7FFF00),
        C("Chocolate", 0xD2691E), C("Coral", 0xFF7F50), C("CornflowerBlue", 0x6495ED), C("Cornsilk", 0xFFF8DC), C("Crimson", 0xDC143C),
        C("Cyan", 0x00FFFF), C("DarkBlue", 0x00008B), C("DarkCyan", 0x008B8B), C("DarkGoldenrod", 0xB8860B), C("DarkGray", 0xA9A9A9),
        C("DarkGreen", 0x006400), C("DarkKhaki", 0xBDB76B), C("DarkMagenta", 0x8B008B), C("DarkOliveGreen", 0x556B2F),
        C("DarkOrange", 0xFF8C00), C("DarkOrchid", 0x9932CC), C("DarkRed", 0x8B0000), C("DarkSalmon", 0xE9967A),
        C("DarkSeaGreen", 0x8FBC8F), C("DarkSlateBlue", 0x483D8B), C("DarkSlateGray", 0x2F4F4F), C("DarkTurquoise", 0x00CED1),
        C("DarkViolet", 0x9400D3), C("DeepPink", 0xFF1493), C("DeepSkyBlue", 0x00BFFF), C("DimGray", 0x696969),
        C("DodgerBlue", 0x1E90FF), C("Firebrick", 0xB22222), C("FloralWhite", 0xFFFAF0), C("ForestGreen", 0x228B22),
        C("Fuchsia", 0xFF00FF), C("Gainsboro", 0xDCDCDC), C("GhostWhite", 0xF8F8FF), C("Gold", 0xFFD700), C("Goldenrod", 0xDAA520),
        C("Gray", 0x808080), C("Green", 0x008000), C("GreenYellow", 0xADFF2F), C("Honeydew", 0xF0FFF0), C("HotPink", 0xFF69B4),
        C("IndianRed", 0xCD5C5C), C("Indigo", 0x4B0082), C("Ivory", 0xFFFFF0), C("Khaki", 0xF0E68C), C("Lavender", 0xE6E6FA),
        C("LavenderBlush", 0xFFF0F5), C("LawnGreen", 0x7CFC00), C("LemonChiffon", 0xFFFACD), C("LightBlue", 0xADD8E6),
        C("LightCoral", 0xF08080), C("LightCyan", 0xE0FFFF), C("LightGoldenrodYellow", 0xFAFAD2), C("LightGray", 0xD3D3D3),
        C("LightGreen", 0x90EE90), C("LightPink", 0xFFB6C1), C("LightSalmon", 0xFFA07A), C("LightSeaGreen", 0x20B2AA),
        C("LightSkyBlue", 0x87CEFA), C("LightSlateGray", 0x778899), C("LightSteelBlue", 0xB0C4DE), C("LightYellow", 0xFFFFE0),
        C("Lime", 0x00FF00), C("LimeGreen", 0x32CD32), C("Linen", 0xFAF0E6), C("Magenta", 0xFF00FF), C("Maroon", 0x800000),
        C("MediumAquamarine", 0x66CDAA), C("MediumBlue", 0x0000CD), C("MediumOrchid", 0xBA55D3), C("MediumPurple", 0x9370DB),
        C("MediumSeaGreen", 0x3CB371), C("MediumSlateBlue", 0x7B68EE), C("MediumSpringGreen", 0x00FA9A),
        C("MediumTurquoise", 0x48D1CC), C("MediumVioletRed", 0xC71585), C("MidnightBlue", 0x191970), C("MintCream", 0xF5FFFA),
        C("MistyRose", 0xFFE4E1), C("Moccasin", 0xFFE4B5), C("NavajoWhite", 0xFFDEAD), C("Navy", 0x000080), C("OldLace", 0xFDF5E6),
        C("Olive", 0x808000), C("OliveDrab", 0x6B8E23), C("Orange", 0xFFA500), C("OrangeRed", 0xFF4500), C("Orchid", 0xDA70D6),
        C("PaleGoldenrod", 0xEEE8AA), C("PaleGreen", 0x98FB98), C("PaleTurquoise", 0xAFEEEE), C("PaleVioletRed", 0xDB7093),
        C("PapayaWhip", 0xFFEFD5), C("PeachPuff", 0xFFDAB9), C("Peru", 0xCD853F), C("Pink", 0xFFC0CB), C("Plum", 0xDDA0DD),
        C("PowderBlue", 0xB0E0E6), C("Purple", 0x800080), C("Red", 0xFF0000), C("RosyBrown", 0xBC8F8F), C("RoyalBlue", 0x4169E1),
        C("SaddleBrown", 0x8B4513), C("Salmon", 0xFA8072), C("SandyBrown", 0xF4A460), C("SeaGreen", 0x2E8B57), C("SeaShell", 0xFFF5EE),
        C("Sienna", 0xA0522D), C("Silver", 0xC0C0C0), C("SkyBlue", 0x87CEEB), C("SlateBlue", 0x6A5ACD), C("SlateGray", 0x708090),
        C("Snow", 0xFFFAFA), C("SpringGreen", 0x00FF7F), C("SteelBlue", 0x4682B4), C("Tan", 0xD2B48C), C("Teal", 0x008080),
        C("Thistle", 0xD8BFD8), C("Tomato", 0xFF6347), C("Turquoise", 0x40E0D0), C("Violet", 0xEE82EE), C("Wheat", 0xF5DEB3),
        C("White", 0xFFFFFF), C("WhiteSmoke", 0xF5F5F5), C("Yellow", 0xFFFF00), C("YellowGreen", 0x9ACD32),
        C("RebeccaPurple", 0x663399),
    ];

    private static readonly Dictionary<string, RoleColorName> ColorsByName =
        Colors.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Цвет из списка по названию (регистр не важен); нет такого — null.</summary>
    public static RoleColorName? FindColor(string? name) =>
        name is not null && ColorsByName.TryGetValue(name.Trim(), out var color) ? color : null;

    /// <summary>
    /// Разобрать цвет роли так, как его поймёт сервер: название из <see cref="Colors"/> (регистр не важен), «#RRGGBB»,
    /// «R, G, B» или «A, R, G, B» (числа 0–255; прозрачность для образца не нужна). Всё прочее — «не распознан»:
    /// сервер с таким значением конфиг не прочитает. Разбор намеренно строже серверного — лишнего не пропустит.
    /// </summary>
    public static bool TryParseColor(string? text, out RoleRgb rgb)
    {
        rgb = default;
        var s = text?.Trim() ?? "";
        if (s.Length == 0) return false;

        if (ColorsByName.TryGetValue(s, out var named))
        {
            rgb = named.Rgb;
            return true;
        }

        if (s[0] == '#')
        {
            // AllowHexSpecifier — только цифры: ни пробелов, ни знака
            if (s.Length != 7 || !uint.TryParse(s.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
                return false;
            rgb = FromNumber(value);
            return true;
        }

        var parts = s.Split(',');
        if (parts.Length is not (3 or 4)) return false;
        var bytes = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!byte.TryParse(parts[i].AsSpan().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i]))
                return false;

        rgb = new RoleRgb(bytes[^3], bytes[^2], bytes[^1]);
        return true;
    }

    /// <summary>Режимы игры (DefaultGameMode роли хранит число).</summary>
    public static IReadOnlyList<int> GameModes { get; } = [0, 1, 2, 3];

    /// <summary>Ключ подписи режима игры: «gamemode.1».</summary>
    public static string GameModeLabelKey(int value) => "gamemode." + value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Привилегии, которых нет в основном списке игры, но сервер их проверяет.
    /// </summary>
    public static IReadOnlyList<string> ExtraPrivileges { get; } = ["ignoremaxclients", "staffentitlement", "denybreakreinforced"];

    /// <summary>Всё, что программа умеет подписать: основной список, затем дополнительные.</summary>
    public static IReadOnlyList<string> AllPrivileges { get; } = [.. ServerConfigSchema.KnownPrivileges, .. ExtraPrivileges];

    private static readonly HashSet<string> Known = new(AllPrivileges, StringComparer.Ordinal);

    /// <summary>Привилегия из списков программы (регистр важен, как и в игре).</summary>
    public static bool IsKnownPrivilege(string code) => Known.Contains(code);

    /// <summary>Не право, а запрет: «выдать все» такую привилегию не трогает (и сервер ролям с AutoGrant её не выдаёт).</summary>
    public static bool IsRestriction(string code) => code == "denybreakreinforced";

    /// <summary>Ключи подписи и подсказки привилегии: «priv.build», «priv.build.hint».</summary>
    public static string PrivilegeLabelKey(string code) => "priv." + code;

    public static string PrivilegeHintKey(string code) => PrivilegeLabelKey(code) + ".hint";

    /// <summary>
    /// Привилегии для показа: известные в порядке игры, дополнительные, затем встретившиеся в файле незнакомые
    /// (от модов) — в порядке появления, без повторов. Так привилегия мода не пропадает из виду.
    /// </summary>
    public static IReadOnlyList<string> PrivilegesToShow(IEnumerable<string> fromFile)
    {
        var seen = new HashSet<string>(Known, StringComparer.Ordinal);
        return [.. AllPrivileges, .. fromFile.Where(code => !string.IsNullOrWhiteSpace(code) && seen.Add(code))];
    }

    /// <summary>
    /// Годится ли код для роли: не пустой, без пробелов и не занят другой ролью (без учёта регистра).
    /// <paramref name="otherCodes"/> — коды остальных ролей.
    /// </summary>
    public static RoleCodeProblem CheckCode(string? code, IEnumerable<string> otherCodes)
    {
        if (string.IsNullOrWhiteSpace(code)) return RoleCodeProblem.Empty;
        if (code.Any(char.IsWhiteSpace)) return RoleCodeProblem.Whitespace;
        return otherCodes.Any(other => string.Equals(other, code, StringComparison.OrdinalIgnoreCase))
            ? RoleCodeProblem.Taken
            : RoleCodeProblem.None;
    }

    private static RoleColorName C(string name, uint rgb) => new(name, FromNumber(rgb));

    private static RoleRgb FromNumber(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}

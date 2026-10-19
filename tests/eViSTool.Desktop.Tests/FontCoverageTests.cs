using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Platform;
using SkiaSharp;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Тексты окна рисуются вшитыми шрифтами (docs/design.md, раздел 4): каждый знак есть в Noto Sans или в Cascadia Mono —
/// его первом запасном (<see cref="Program.FontOptions"/>). Знак, которого нет ни в одном, Avalonia берёт из системы: на
/// другой машине он другой, а у знаков с вариантом-эмодзи («↗», «✔», «⚠») — цветной квадрат Segoe UI Emoji, как только
/// окно хоть раз показало эмодзи из названия мода. Так «↗» в «О программе» и стал синим.
/// <para>Тексты — значения Lang (ru, en), вся разметка *.axaml без комментариев и строки в коде окна и моделей
/// (Desktop, Presentation). Покрытие — по самим файлам шрифтов (cmap), без Avalonia.</para>
/// </summary>
public sealed class FontCoverageTests
{
    private static readonly string Root = ThemeTests.RepoRoot();

    /// <summary>
    /// Законные исключения. Буквы письменностей, которых во вшитых шрифтах нет вовсе: названия языков сервера на самих
    /// этих языках («العربية», «ไทย», «日本語», «한국어» — cfg.ServerLanguage.choice.*). Их рисует система; у букв нет
    /// вариантов-эмодзи, а вшивать ради девяти слов шрифты CJK (десятки мегабайт) незачем.
    /// </summary>
    private static bool ForeignScriptLetter(int codepoint) =>
        Rune.GetUnicodeCategory(new Rune(codepoint)) is UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
        && (codepoint is >= 0x0600 and <= 0x06FF   // арабское письмо
            or >= 0x0E00 and <= 0x0E7F             // тайское
            or >= 0x3040 and <= 0x30FF             // кана
            or >= 0x4E00 and <= 0x9FFF             // иероглифы
            or >= 0xAC00 and <= 0xD7AF);           // хангыль

    /// <summary>
    /// Законные исключения в коде: строки оповещений для мессенджеров (Telegram, Discord) — окно их не рисует, а знаки
    /// подобраны под шрифты мессенджеров (см. Core/Notifications/NotifyChannels).
    /// </summary>
    private static readonly (string File, string Glyph)[] MessengerGlyphs =
    [
        ("eViSTool.Presentation/ViewModels/HostedWatch.cs", "⚑"),
        ("eViSTool.Presentation/ViewModels/HostedWatch.cs", "✗"),
        ("eViSTool.Presentation/ViewModels/HostedWatch.Backups.cs", "⚑"),
    ];

    [Fact]
    public void Ui_texts_use_only_glyphs_of_the_embedded_fonts()
    {
        using var noto = SKTypeface.FromFile(Path.Combine(Root, "src", "eViSTool.Desktop", "Assets", "Fonts", "NotoSans.ttf"));
        using var mono = SKTypeface.FromFile(Path.Combine(Root, "src", "eViSTool.Desktop", "Assets", "Fonts", "CascadiaMono.ttf"));
        using var symbols = SKTypeface.FromFile(Path.Combine(Root, "src", "eViSTool.Desktop", "Assets", "Fonts", "NotoSansSymbols2.ttf"));
        Assert.Equal("Noto Sans", noto.FamilyName);
        Assert.Equal("Cascadia Mono", mono.FamilyName);
        Assert.Equal("Noto Sans Symbols 2", symbols.FamilyName);

        var missing = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var checkedTexts = 0;
        foreach (var (where, text) in UiTexts())
        {
            checkedTexts++;
            foreach (var rune in text.EnumerateRunes())
            {
                var cp = rune.Value;
                if (Rune.IsControl(rune) || cp == 0xFEFF || ForeignScriptLetter(cp)) continue;
                if (noto.ContainsGlyph(cp) || mono.ContainsGlyph(cp) || symbols.ContainsGlyph(cp)) continue;
                if (MessengerGlyphs.Contains((where, rune.ToString()))) continue;
                var key = $"U+{cp:X4} «{rune}»";
                if (!missing.TryGetValue(key, out var places)) missing[key] = places = new SortedSet<string>(StringComparer.Ordinal);
                places.Add(where);
            }
        }

        Assert.True(checkedTexts > 2000, $"текстов проверено: {checkedTexts}"); // проверка видит тексты, а не пустоту
        Assert.True(missing.Count == 0, "нет ни в одном вшитом шрифте (Noto Sans, Cascadia Mono, Noto Sans Symbols 2):\n" +
            string.Join("\n", missing.Select(m => $"{m.Key}: {string.Join(", ", m.Value.Take(8))}")));
    }

    /// <summary>Сама проверка покрытия: знаки, ради которых Cascadia Mono стал запасным, и «↗», которого нет ни в одном.</summary>
    [Fact]
    public void Coverage_check_reads_the_font_files()
    {
        using var noto = SKTypeface.FromFile(Path.Combine(Root, "src", "eViSTool.Desktop", "Assets", "Fonts", "NotoSans.ttf"));
        using var mono = SKTypeface.FromFile(Path.Combine(Root, "src", "eViSTool.Desktop", "Assets", "Fonts", "CascadiaMono.ttf"));
        using var symbols = SKTypeface.FromFile(Path.Combine(Root, "src", "eViSTool.Desktop", "Assets", "Fonts", "NotoSansSymbols2.ttf"));
        Assert.True(noto.ContainsGlyph('Ж') && noto.ContainsGlyph('«') && noto.ContainsGlyph('—'));
        // кусочек Noto Sans Symbols 2 — ровно знаки, которых нет в двух других
        foreach (var sign in "⚠★⬇✔✖")
            Assert.True(symbols.ContainsGlyph(sign) && !noto.ContainsGlyph(sign) && !mono.ContainsGlyph(sign), $"«{sign}»");
        foreach (var arrow in "→←↑↓▾▲▼▸✓♥")
        {
            Assert.False(noto.ContainsGlyph(arrow), $"«{arrow}» в Noto Sans");
            Assert.True(mono.ContainsGlyph(arrow), $"«{arrow}» нет в Cascadia Mono");
        }
        Assert.False(noto.ContainsGlyph('↗') || mono.ContainsGlyph('↗') || symbols.ContainsGlyph('↗'));
    }

    /// <summary>
    /// Запасной шрифт окна работает в Avalonia: знак, которого нет в Noto Sans, подбирается из вшитого Cascadia Mono, а
    /// не из системы. Менеджер шрифтов — отдельный, с запасными шрифтами окна (Program.FontOptions): у тестового
    /// приложения настройки свои, а передать их новому менеджеру Avalonia 12 даёт только через закрытый AvaloniaLocator —
    /// поэтому ему ставится то же поле, которое заполнил бы конструктор.
    /// </summary>
    [AvaloniaFact]
    public void Window_font_fallback_is_the_embedded_mono_font()
    {
        var options = Program.FontOptions;
        Assert.Equal(Program.UiFont, options.DefaultFamilyName);
        Assert.Equal([new FontFamily(Program.MonoFont), new FontFamily(Program.SymbolsFont)], options.FontFallbacks!.Select(f => f.FontFamily));

        const BindingFlags inside = BindingFlags.Instance | BindingFlags.NonPublic;
        var platform = typeof(FontManager).GetProperty("PlatformImpl", inside)?.GetValue(FontManager.Current) as IFontManagerImpl;
        var fallbacks = typeof(FontManager).GetField("_fontFallbacks", inside);
        Assert.SkipWhen(platform is null || fallbacks is null, "у этой версии Avalonia менеджер шрифтов устроен иначе");
        var fonts = new FontManager(platform);
        fallbacks.SetValue(fonts, options.FontFallbacks);

        var ui = new FontFamily(Program.UiFont);
        string Match(char c) =>
            fonts.TryMatchCharacter(c, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, ui, CultureInfo.CurrentUICulture, out var typeface)
            && fonts.TryGetGlyphTypeface(typeface, out var glyphs) ? glyphs.FamilyName : "—";
        foreach (var c in "→▾✓♥")
            Assert.Equal(("Cascadia Mono", c), (Match(c), c));
        foreach (var c in "⚠★⬇✔✖")
            Assert.Equal(("Noto Sans Symbols 2", c), (Match(c), c));
        // в основном шрифте знак есть — запасной не нужен: Avalonia спрашивает запасные только о недостающих знаках
        Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(ui), out var noto));
        Assert.Equal("Noto Sans", noto.FamilyName);
        Assert.True(noto.CharacterToGlyphMap.TryGetGlyph('Ж', out _));
    }

    // ---------- откуда тексты

    private static IEnumerable<(string Where, string Text)> UiTexts()
    {
        var src = Path.Combine(Root, "src");
        foreach (var lang in new[] { "ru", "en" })
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(src, "eViSTool.Core", "Lang", lang + ".json")));
            foreach (var entry in json.RootElement.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.String) yield return ($"{lang}.json {entry.Name}", entry.Value.GetString()!);
        }
        foreach (var file in SourceFiles(Path.Combine(src, "eViSTool.Desktop"), "*.axaml"))
            yield return (Relative(src, file), Regex.Replace(File.ReadAllText(file), "<!--.*?-->", "", RegexOptions.Singleline));
        foreach (var project in new[] { "eViSTool.Desktop", "eViSTool.Presentation" })
            foreach (var file in SourceFiles(Path.Combine(src, project), "*.cs"))
                foreach (var literal in StringLiterals(File.ReadAllText(file)))
                    yield return (Relative(src, file), literal);
    }

    private static IEnumerable<string> SourceFiles(string dir, string pattern) =>
        Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
            .Where(f => !Relative(dir, f).StartsWith("bin/") && !Relative(dir, f).StartsWith("obj/"));

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    /// <summary>
    /// Строковые литералы C# без комментариев: обычные, @"…", $"…" (строки внутри {…} — отдельными литералами),
    /// """…""" и символы '…'. Разбор простой, без компилятора: на нынешнем коде его хватает (String_literals_skip_comments).
    /// </summary>
    internal static List<string> StringLiterals(string code)
    {
        var found = new List<string>();
        var i = 0;
        Code(insideHole: false);
        return found;

        char At(int k) => k < code.Length ? code[k] : '\0';

        // код до конца файла, а в {…} интерполяции — до её закрывающей скобки
        void Code(bool insideHole)
        {
            var depth = 0;
            while (i < code.Length)
            {
                var c = code[i];
                if (c == '/' && At(i + 1) == '/') { i = code.IndexOf('\n', i) is var end and >= 0 ? end : code.Length; continue; }
                if (c == '/' && At(i + 1) == '*') { i = code.IndexOf("*/", i + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 2 : code.Length; continue; }
                if (c == '\'')
                {
                    var end = i + 1;
                    while (end < code.Length && code[end] != '\'' && code[end] != '\n') end += code[end] == '\\' ? 2 : 1;
                    found.Add(code[(i + 1)..Math.Min(end, code.Length)]);
                    i = end + 1;
                    continue;
                }
                if (c == '"') { Literal(); continue; }
                if (insideHole && c == '{') depth++;
                if (insideHole && c == '}' && depth-- == 0) return;
                i++;
            }
        }

        void Literal()
        {
            bool interpolated = false, verbatim = false;
            for (var k = i - 1; k >= 0 && code[k] is '$' or '@'; k--)
                if (code[k] == '$') interpolated = true;
                else verbatim = true;
            var quotes = 0;
            while (At(i + quotes) == '"') quotes++;
            if (quotes >= 3 && !verbatim)
            {
                // сырая строка — до такой же череды кавычек (дыры $$"""{{…}}""" остаются в тексте: там код, а не знаки)
                var close = code.IndexOf(new string('"', quotes), i + quotes, StringComparison.Ordinal);
                var stop = close >= 0 ? close : code.Length;
                found.Add(code[(i + quotes)..stop]);
                i = stop + quotes;
                return;
            }
            var text = new StringBuilder();
            i++;
            while (i < code.Length)
            {
                var c = code[i];
                if (verbatim && c == '"' && At(i + 1) == '"') { text.Append('"'); i += 2; continue; }
                if (!verbatim && c == '\\') { text.Append(c).Append(At(i + 1)); i += 2; continue; }
                if (c == '"') { i++; break; }
                if (!verbatim && c == '\n') break;
                if (interpolated && c is '{' or '}' && At(i + 1) == c) { text.Append(c); i += 2; continue; }
                if (interpolated && c == '{') { i++; Code(insideHole: true); i++; continue; }
                text.Append(c);
                i++;
            }
            found.Add(text.ToString());
        }
    }

    [Fact]
    public void String_literals_skip_comments()
    {
        var code = """
            // «↗» в комментарии не текст
            var a = "→ дальше"; /* и «✔» тоже */ var b = @"путь ""в"" кавычках"; var c = '▾';
            var d = $"{x} — {(y ? "да" : "нет")}";
            """;
        var literals = StringLiterals(code).ToList();
        Assert.Contains("→ дальше", literals);
        Assert.Contains(@"путь ""в"" кавычках", literals);
        Assert.Contains("▾", literals);
        Assert.Contains("да", literals);
        Assert.DoesNotContain(literals, l => l.Contains('↗') || l.Contains('✔'));
    }
}

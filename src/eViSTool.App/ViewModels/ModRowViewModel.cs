using System.Net;
using System.Text.RegularExpressions;
using eViSTool.Core.Mods;

namespace eViSTool.App.ViewModels;

/// <summary>Строка таблицы модов.</summary>
public sealed partial class ModRowViewModel(ModCheckResult r)
{
    public ModStatus Kind { get; } = r.Status;
    public bool IsDuplicate { get; } = r.IsDuplicate;

    public string Name { get; } = r.Local.Info?.Name is { Length: > 0 } n ? n : r.Local.FileName;
    public string ModId { get; } = r.Local.Info?.ModId ?? "";
    public string FileName { get; } = r.Local.FileName;
    public string Installed { get; } = r.Local.Info?.Version ?? "";
    public string Latest { get; } = r.LatestCompatible?.ModVersion ?? "";
    public string? PageUrl { get; } = r.Remote?.PageUrl;

    public string StatusText { get; } = r.Status switch
    {
        ModStatus.UpToDate => "Актуален",
        ModStatus.UpdateAvailable => "Есть обновление",
        ModStatus.NoCompatibleRelease => "Нет версии для игры",
        ModStatus.NotInModDb => "Нет в модбазе",
        ModStatus.Unreadable => "Не читается",
        ModStatus.CheckFailed => "Ошибка проверки",
        _ => r.Status.ToString(),
    };

    public string Note { get; } = string.Join(" · ", new[]
    {
        r.IsDuplicate ? "Дубликат: этот modid установлен несколько раз" : null,
        r.Message,
        r.LatestAny is { } any ? $"Есть {any.ModVersion} для другой версии игры ({string.Join(", ", any.GameVersions.TakeLast(1))})" : null,
    }.Where(s => !string.IsNullOrEmpty(s)));

    public string? Changelog { get; } = r.Status == ModStatus.UpdateAvailable ? StripHtml(r.LatestCompatible?.Changelog) : null;

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = BrTags().Replace(html, "\n");
        text = Tags().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Trim();
        return text.Length > 1500 ? text[..1500] + "…" : text;
    }

    [GeneratedRegex(@"<\s*(br|/p|/li)\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrTags();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}

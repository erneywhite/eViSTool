using eViSTool.Core.Mods;
using eViSTool.Core.Localization;

namespace eViSTool.App.ViewModels;

/// <summary>Строка таблицы модов.</summary>
public sealed class ModRowViewModel(ModCheckResult r)
{
    public ModCheckResult Result { get; } = r;
    public LocalMod Local { get; } = r.Local;
    public ModStatus Kind { get; } = r.Status;
    public bool IsDuplicate { get; } = r.IsDuplicate;
    public bool IsEnabled { get; } = !r.Local.IsDisabled;

    public string Name { get; } = r.Local.Info?.Name is { Length: > 0 } n ? n : r.Local.FileName;
    public string ModId { get; } = r.Local.Info?.ModId ?? "";
    public string FileName { get; } = r.Local.FileName;
    public string FilePath { get; } = r.Local.Path;
    public string Installed { get; } = r.Local.Info?.Version ?? "";
    public string Latest { get; } = r.LatestCompatible?.ModVersion ?? "";
    public string? PageUrl { get; } = r.Remote?.PageUrl;

    /// <summary>Для фильтра «только требующие внимания».</summary>
    public bool NeedsAttention { get; } = r.IsDuplicate || r.Status is not (ModStatus.UpToDate or ModStatus.NotChecked or ModStatus.Pinned);

    /// <summary>Можно обновить одной кнопкой: есть совместимый релиз с файлом.</summary>
    public bool CanUpdate { get; } = r.Status == ModStatus.UpdateAvailable && r.LatestCompatible?.MainFile is not null;

    public string StatusText { get; } = r.Status switch
    {
        ModStatus.UpToDate => Loc.T("status.upToDate"),
        ModStatus.UpdateAvailable => Loc.T("status.updateAvailable"),
        ModStatus.NoCompatibleRelease => Loc.T("status.noCompatible"),
        ModStatus.NotInModDb => Loc.T("status.notInModDb"),
        ModStatus.Unreadable => Loc.T("status.unreadable"),
        ModStatus.CheckFailed => Loc.T("status.checkFailed"),
        ModStatus.NotChecked => Loc.T("status.notChecked"),
        ModStatus.Pinned => Loc.T("status.pinned"),
        _ => r.Status.ToString(),
    };

    public string Note { get; } = string.Join(" · ", new[]
    {
        r.Local.IsDisabled ? Loc.T("note.disabled") : null,
        r.IsDuplicate ? Loc.T("note.duplicate") : null,
        r.Message,
        r.LatestAny is { } any ? Loc.T("note.newerElsewhere", any.ModVersion, string.Join(", ", any.GameVersions.TakeLast(1))) : null,
    }.Where(s => !string.IsNullOrEmpty(s)));

    public string? Changelog { get; } = r.Status == ModStatus.UpdateAvailable ? Html.ToPlainText(r.LatestCompatible?.Changelog, 1500) : null;
}

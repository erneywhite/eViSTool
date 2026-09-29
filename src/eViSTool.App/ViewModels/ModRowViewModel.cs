using eViSTool.Core.Mods;

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
        ModStatus.UpToDate => "Актуален",
        ModStatus.UpdateAvailable => "Есть обновление",
        ModStatus.NoCompatibleRelease => "Нет версии для игры",
        ModStatus.NotInModDb => "Нет в модбазе",
        ModStatus.Unreadable => "Не читается",
        ModStatus.CheckFailed => "Ошибка проверки",
        ModStatus.NotChecked => "Не проверялся",
        ModStatus.Pinned => "Закреплён",
        _ => r.Status.ToString(),
    };

    public string Note { get; } = string.Join(" · ", new[]
    {
        r.Local.IsDisabled ? "Выключен в настройках игры" : null,
        r.IsDuplicate ? "Дубликат: этот modid установлен несколько раз" : null,
        r.Message,
        r.LatestAny is { } any ? $"Есть {any.ModVersion} для другой версии игры ({string.Join(", ", any.GameVersions.TakeLast(1))})" : null,
    }.Where(s => !string.IsNullOrEmpty(s)));

    public string? Changelog { get; } = r.Status == ModStatus.UpdateAvailable ? Html.ToPlainText(r.LatestCompatible?.Changelog, 1500) : null;
}

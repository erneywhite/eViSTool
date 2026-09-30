using eViSTool.Core.Localization;

namespace eViSTool.App;

/// <summary>Размеры файлов человеческим текстом: «1,3 ГБ».</summary>
public static class Sizes
{
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => Loc.T("size.bytes", bytes),
        < 1024 * 1024 => Loc.T("size.kb", (bytes / 1024.0).ToString("0")),
        < 1024L * 1024 * 1024 => Loc.T("size.mb", (bytes / 1024.0 / 1024).ToString("0.#")),
        _ => Loc.T("size.gb", (bytes / 1024.0 / 1024 / 1024).ToString("0.##")),
    };
}

using eViSTool.Core.Localization;

namespace eViSTool.App;

/// <summary>Размеры файлов человеческим текстом: «1,3 ГБ» (сама логика — в ядре, она нужна и агенту).</summary>
public static class Sizes
{
    public static string Format(long bytes) => SizeText.Format(bytes);
}

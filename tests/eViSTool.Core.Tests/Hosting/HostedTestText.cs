using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// Тексты ключа на всех языках — прямо из словарей в src. Тесты Core идут параллельно, и соседний тест может
/// переключить язык окна посреди проверки: сравнивать с Loc.T здесь нельзя, а с любым из переводов — можно.
/// </summary>
internal static class HostedTestText
{
    private static readonly Lazy<JObject[]> Dictionaries = new(() =>
    {
        var lang = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "eViSTool.Core", "Lang"));
        return [.. new[] { "en.json", "ru.json" }.Select(f => JObject.Parse(File.ReadAllText(Path.Combine(lang, f))))];
    });

    /// <summary>Переводы ключа без подстановок (фигурные места остаются как есть).</summary>
    public static string[] Of(string key) => [.. Dictionaries.Value.Select(d => d.Value<string>(key) ?? throw new KeyNotFoundException(key))];

    /// <summary>Текст — один из переводов ключа.</summary>
    public static void AssertIs(string key, string actual) => Assert.Contains(actual, Of(key));
}
